﻿using Flurl.Http;
using MongoDB.Driver;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VentilatorDaemon
{
    public class CalculatedValues
    {
        [JsonProperty("IE")]
        public double IE { get; set; }
        [JsonProperty("tidalVolume")]
        public double TidalVolume { get; set; }
        [JsonProperty("volumePerMinute")]
        public double VolumePerMinute { get; set; }
        [JsonProperty("respatoryRate")]
        public double RespatoryRate { get; set; }
        [JsonProperty("pressurePlateau")]
        public double PressurePlateau { get; set; }

        public void ResetValues()
        {
            IE = 0.0;
            VolumePerMinute = 0.0;
            RespatoryRate = 0.0;
            PressurePlateau = 0.0;
        }
    }

    public class ProcessingThread
    {
        private readonly MongoClient client;
        private readonly IMongoDatabase database;
        private readonly SerialThread serialThread;
        private readonly WebSocketThread webSocketThread;

        private readonly uint BPM_TOO_LOW = 1;
        private readonly uint PEEP_NOT_OK = 16;
        private readonly uint PRESSURE_NOT_OK = 32;
        private readonly uint VOLUME_NOT_OK = 64;
        private readonly uint RESIDUAL_VOLUME_NOT_OK = 128;

        readonly FlurlClient flurlClient;

        public ProcessingThread(SerialThread serialThread, WebSocketThread webSocketThread, string databaseHost, string webServerHost)
        {
            flurlClient = new FlurlClient($"http://{webServerHost}:3001");
            client = new MongoClient($"mongodb://{databaseHost}:27017/?connect=direct;replicaSet=rs0;readPreference=primaryPreferred");
            database = client.GetDatabase("beademing");
            this.serialThread = serialThread;
            this.webSocketThread = webSocketThread;
        }

        private List<ValueEntry> GetDocuments(string collection, int number)
        {
            var builder = Builders<ValueEntry>.Filter;

            return database.GetCollection<ValueEntry>(collection)
                .Find<ValueEntry>(builder.Empty)
                .SortByDescending(entry => entry.LoggedAt)
                .Limit(number)
                .ToList();

        }

        private List<ValueEntry> GetDocuments(string collection, DateTime since)
        {
            var builder = Builders<ValueEntry>.Filter;

            return database.GetCollection<ValueEntry>(collection)
                .Find<ValueEntry>(builder.Gt(e => e.LoggedAt, since))
                .SortByDescending(entry => entry.LoggedAt)
                .ToList();

        }

        private double GetMaximum(List<ValueEntry> values, Func<ValueEntry, double> comparison, DateTime startDateTime, DateTime endDateTime)
        {
            return values
                .Where(v => v.LoggedAt >= startDateTime && v.LoggedAt <= endDateTime)
                .Max(v => comparison(v));
        }

        private double GetMinimum(List<ValueEntry> values, Func<ValueEntry, double> comparison, DateTime startDateTime, DateTime endDateTime)
        {
            return values
                .Where(v => v.LoggedAt >= startDateTime && v.LoggedAt <= endDateTime)
                .Min(v => comparison(v));
        }

        private List<Tuple<DateTime, DateTime>> GetBreathingCyclesFromTargetPressure(List<ValueEntry> values, DateTime maxDateTime)
        {
            List<Tuple<DateTime, DateTime>> breathingCycles = new List<Tuple<DateTime, DateTime>>();
            DateTime? startBreathingCycle = null;
            DateTime? endBreathingCycle = null;
            var lastValue = 0.0;
            var firstLoop = true;
            var goingUp = false;
            var previousLoggedAt = DateTime.Now;

            foreach (var valueEntry in values)
            {
                if (valueEntry.LoggedAt <= maxDateTime)
                {
                    if (firstLoop)
                    {
                        lastValue = valueEntry.Value.TargetPressure;
                        previousLoggedAt = valueEntry.LoggedAt;
                        firstLoop = false;
                    }
                    else
                    {
                        if (valueEntry.Value.TargetPressure > lastValue)
                        {
                            if (!goingUp)
                            {
                                // positive edge
                                startBreathingCycle = endBreathingCycle;
                                endBreathingCycle = previousLoggedAt;
                                goingUp = true;

                                if (startBreathingCycle.HasValue && endBreathingCycle.HasValue)
                                {
                                    breathingCycles.Add(Tuple.Create(startBreathingCycle.Value, endBreathingCycle.Value));
                                }
                            }
                        }
                        else if (valueEntry.Value.TargetPressure < lastValue)
                        {
                            goingUp = false;
                        }

                        lastValue = valueEntry.Value.TargetPressure;
                        previousLoggedAt = valueEntry.LoggedAt;
                    }
                }
            }

            return breathingCycles;
        }

        public Task Start(CancellationToken cancellationToken)
        {
            CalculatedValues calculatedValues = new CalculatedValues();

            return Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var start = DateTime.Now;
                    try
                    {
                        var settings = webSocketThread.Settings;
                        calculatedValues.ResetValues();

                        // are we active?
                        if (!(settings.ContainsKey("ACTIVE") && settings["ACTIVE"] > 1.0f))
                        {
                            // make sure we stop playing the alarm
                            serialThread.ResetAlarm();
                            await Task.Delay(2000);
                            continue;
                        }

                        uint alarmBits = 0;

                        var values = GetDocuments("measured_values", DateTime.UtcNow.AddSeconds(-70));

                        if (values.Count == 0)
                        {
                            // no data yet, wait for it to become available
                            await Task.Delay(2000);
                            continue;
                        }

                        // find the lowest datetime value that we all have
                        var maxDateTime = values.First().LoggedAt;

                        values.Reverse();

                        // check breaths per minute
                        List<Tuple<DateTime, DateTime>> breathingCycles = GetBreathingCyclesFromTargetPressure(values, maxDateTime);

                        // do we have a full cycle
                        if (breathingCycles.Count > 0)
                        {
                            DateTime startBreathingCycle = breathingCycles.Last().Item1;
                            DateTime endBreathingCycle = breathingCycles.Last().Item2;

                            var minValTargetPressure = GetMinimum(values, (valueEntry) => valueEntry.Value.TargetPressure, startBreathingCycle, endBreathingCycle);

                            var maxValTargetPressure = GetMaximum(values, (valueEntry) => valueEntry.Value.TargetPressure, startBreathingCycle, endBreathingCycle);

                            var targetPressureExhale = values
                                .Where(v => v.LoggedAt > startBreathingCycle && v.LoggedAt <= endBreathingCycle && v.Value.TargetPressure == minValTargetPressure)
                                .FirstOrDefault();

                            DateTime exhalemoment = targetPressureExhale.LoggedAt.AddMilliseconds(-40);

                            var breathingCycleDuration = (endBreathingCycle - startBreathingCycle).TotalSeconds;
                            var bpm = 60.0 / breathingCycleDuration;

                            calculatedValues.RespatoryRate = bpm;

                            if (settings.ContainsKey("RR"))
                            {
                                if (bpm <= settings["RR"] - 1.0)
                                {
                                    alarmBits |= BPM_TOO_LOW;
                                }
                            }

                            var inhaleTime = (exhalemoment - startBreathingCycle).TotalSeconds;
                            var exhaleTime = (endBreathingCycle - exhalemoment).TotalSeconds;

                            calculatedValues.IE = inhaleTime / breathingCycleDuration;

                            var tidalVolume = GetMaximum(values, (valueEntry) => valueEntry.Value.Volume, startBreathingCycle, endBreathingCycle);

                            if (settings.ContainsKey("VT") && settings.ContainsKey("ADVT"))
                            {
                                if (Math.Abs(tidalVolume - settings["VT"]) > settings["ADVT"])
                                {
                                    alarmBits |= VOLUME_NOT_OK;
                                }
                            }

                            calculatedValues.TidalVolume = tidalVolume;

                            var residualVolume = GetMinimum(values, (valueEntry) => valueEntry.Value.Volume, exhalemoment, endBreathingCycle.AddMilliseconds(-80));

                            if (Math.Abs(residualVolume) > 50)
                            {
                                alarmBits |= RESIDUAL_VOLUME_NOT_OK;
                            }

                            var peakPressureMoment = values
                               .Where(v => v.LoggedAt >= startBreathingCycle && v.LoggedAt <= exhalemoment)
                               .Aggregate((i1, i2) => i1.Value.Pressure > i2.Value.Pressure ? i1 : i2);

                            var plateauMinimumPressure = GetMinimum(values, (valueEntry) => valueEntry.Value.Pressure, peakPressureMoment.LoggedAt, exhalemoment);

                            if (settings.ContainsKey("ADPK"))
                            {
                                if (!(Math.Abs(peakPressureMoment.Value.Pressure - maxValTargetPressure) < settings["ADPK"]
                                    && Math.Abs(plateauMinimumPressure - maxValTargetPressure) < settings["ADPK"]))
                                {
                                    alarmBits |= PRESSURE_NOT_OK;
                                }
                            }

                            calculatedValues.PressurePlateau = plateauMinimumPressure;

                            //did we have a trigger within this cycle?
                            var triggerMoment = values
                                .FirstOrDefault(p => p.LoggedAt >= exhalemoment && p.LoggedAt <= endBreathingCycle && p.Value.Trigger > 0.0f);
                            var endPeep = endBreathingCycle;
                            if (triggerMoment != null)
                            {
                                endPeep = triggerMoment.LoggedAt.AddMilliseconds(-50);
                            }

                            if (settings.ContainsKey("PP") && settings.ContainsKey("ADPP"))
                            {
                                bool firstPeepPressureIteration = true;
                                ValueEntry previousPoint = null;
                                List<float> slopes = new List<float>();
                                int plateauCounter = 0;
                                bool foundPlateau = false;

                                var pressureExhaleValues = values
                                    .Where(v => v.LoggedAt >= exhalemoment.AddMilliseconds(100) && v.LoggedAt <= endPeep)
                                    .OrderByDescending(v => v.LoggedAt)
                                    .ToList();

                                for (int i = 0; i < pressureExhaleValues.Count - 2; i += 2)
                                {
                                    var valueEntry = pressureExhaleValues[i];

                                    // if last value is above PEEP, assume everything is ok
                                    if (firstPeepPressureIteration)
                                    {
                                        if (valueEntry.Value.Pressure > settings["PP"] - settings["ADPP"])
                                        {
                                            foundPlateau = true;
                                            break;
                                        }

                                        firstPeepPressureIteration = false;
                                    }

                                    // we are still here, so last value was not in peep threshold
                                    // go back until we are above PEEP again and start calculating slope
                                    // when the average slope is steadily declining, we have a leak

                                    if (previousPoint != null)
                                    {
                                        var gradient = (previousPoint.Value.Pressure - valueEntry.Value.Pressure) / ((float)(previousPoint.LoggedAt - valueEntry.LoggedAt).TotalSeconds);

                                        if (gradient > -1.0f)
                                        {
                                            plateauCounter++;

                                            if (plateauCounter == 3)
                                            {
                                                if (valueEntry.Value.Pressure > settings["PP"] - settings["ADPP"])
                                                {
                                                    foundPlateau = true;
                                                    break;
                                                }
                                                else
                                                {
                                                    plateauCounter = 0;
                                                }
                                            }

                                        }
                                        else
                                        {
                                            plateauCounter = 0;
                                        }
                                    }
                                    previousPoint = valueEntry;
                                }

                                if (!foundPlateau)
                                {
                                    // all points are below peep, clearly we should raise an alarm
                                    alarmBits |= PEEP_NOT_OK;
                                }
                            }

                            // only for debug purposes, send the moments to the frontend
                            //await serialThread.SendSettingToServer("breathingCycleStart", startBreathingCycle.Value.);
                        }

                        serialThread.AlarmValue = alarmBits;

                        
                        if (breathingCycles.Count > 1)
                        {
                            foreach (var breathingCycle in breathingCycles)
                            {
                                var tidalVolume = values
                                    .Where(v => v.LoggedAt >= breathingCycle.Item1 && v.LoggedAt <= breathingCycle.Item2)
                                    .Max(v => v.Value.Volume);

                                calculatedValues.VolumePerMinute += tidalVolume / 1000.0;
                            }

                            calculatedValues.VolumePerMinute = calculatedValues.VolumePerMinute / (breathingCycles.Last().Item2 - breathingCycles.First().Item1).TotalSeconds * 60.0;
                        }


                        await flurlClient.Request("/api/calculated_values")
                            .PutJsonAsync(calculatedValues);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        Console.WriteLine(e.StackTrace);
                    }

                    var timeSpent = (DateTime.Now - start).TotalMilliseconds;
                    // Console.WriteLine("Time taken processing: {0}", timeSpent);
                    await Task.Delay(Math.Max(1, 500 - (int)timeSpent));
                }
            });
        }
    }
}

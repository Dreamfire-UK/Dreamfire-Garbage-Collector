using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace DreamfireGarbageCollector.Scripts
{
    [DefaultExecutionOrder(-20000)]
    public class GcLoadBenchmark : MonoBehaviour
    {
        [Header("External Scripts")]
        [SerializeField] private ManagedGcScheduler scheduler;

        [Header("Load: defaults allocate about 1 MB per frame")]
        [SerializeField, Min(1)] private int allocationsPerFrame = 256;
        [SerializeField, Min(16)] private int bytesPerAllocation = 4096;
        [SerializeField, Min(1)] private int retainedFrames = 30;

        [Header("Test length")]
        [SerializeField, Min(0)] private int warmupFrames = 60;
        [SerializeField, Min(60)] private int measuredFrames = 3600;
        [SerializeField, InspectorName("Scheduler On (one mode per launch)")] private bool schedulerFirst;
        [SerializeField] private bool incrementalBaseline;
        [SerializeField] private bool burstAndCleanup;
        [SerializeField] private int targetFrameRate = 60;

        private byte[][][] retained;
        private bool running;
        private bool hasRun;

        private void Awake()
        {
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (argument == "-gcbench=on") { schedulerFirst = true; incrementalBaseline = false; }
                if (argument == "-gcbench=off") { schedulerFirst = false; incrementalBaseline = false; }
                if (argument == "-gcbench=incremental") { schedulerFirst = false; incrementalBaseline = true; }
                if (argument == "-gcbench-workload=burst") burstAndCleanup = true;
            }
            if (scheduler != null) scheduler.enabled = schedulerFirst;
        }

        private void Start()
        {
            StartBenchmark();
        }

        public void StartBenchmark()
        {
            if (running || hasRun) return;

            if (Application.isEditor)
            {
                Debug.LogError("ManagedGcScheduler does nothing in the Editor. " + "Run this benchmark in a standalone player build.", this);
                return;
            }

            if (schedulerFirst && incrementalBaseline)
            {
                Debug.LogError("Scheduler On and Incremental Baseline cannot both be selected.", this);
                return;
            }

            if (GarbageCollector.isIncremental != incrementalBaseline)
            {
                Debug.LogError("The selected mode does not match this build's Use Incremental GC setting.", this);
                return;
            }

            if (scheduler == null) scheduler = ManagedGcScheduler.Instance;

            if (scheduler == null)
            {
                Debug.LogError("Assign a ManagedGcScheduler.", this);
                return;
            }

            hasRun = true;
            StartCoroutine(RunComparison());
        }

        private IEnumerator RunComparison()
        {
            running = true;

            bool originalSchedulerEnabled = scheduler.enabled;
            int originalVSync = QualitySettings.vSyncCount;
            int originalTargetFrameRate = Application.targetFrameRate;

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = targetFrameRate;
            Recorder gcRecorder = null;
            Recorder incrementalRecorder = null;

            try
            {
                for (int run = 0; run < 1; run++)
                {
                    bool schedulerOn = schedulerFirst;
                    scheduler.enabled = schedulerOn;
                    yield return null;

                    GarbageCollector.Mode expectedMode = schedulerOn ? GarbageCollector.Mode.Manual : GarbageCollector.Mode.Enabled;

                    if (GarbageCollector.GCMode != expectedMode)
                    {
                        Debug.LogError($"Cannot test {ModeName(schedulerOn)}: expected " + $"{expectedMode}, got {GarbageCollector.GCMode}.", this);
                        yield break;
                    }

                    Debug.Log($"GC benchmark started: {ModeName(schedulerOn)}, Unity {Application.unityVersion}, RAM {SystemInfo.systemMemorySize} MB, incremental {GarbageCollector.isIncremental}.", this);

                    retained = new byte[retainedFrames][][];

                    for (int frame = 0; frame < warmupFrames; frame++)
                    {
                        AllocateFrame(frame);
                        yield return null;
                    }

                    Array.Clear(retained, 0, retained.Length);
                    GC.Collect();
                    yield return null;
                    yield return null;

                    retained = new byte[retainedFrames][][];
                    double[] frameTimesMs = new double[measuredFrames];
                    long peakManagedBytes = 0;
                    long peakReservedBytes = 0;
                    long peakUnityReservedBytes = 0;
                    double totalMs = 0;
                    double cleanupMs = 0;
                    int framesOver33Ms = 0;
                    int framesOver50Ms = 0;
                    int framesOver100Ms = 0;
                    int collectionsBefore = scheduler.CollectionCount;
                    int automaticBefore = scheduler.AutomaticCollectionCount;
                    int idleBefore = scheduler.IdleCollectionCount;
                    int gcEvents = 0;
                    double gcTimeMs = 0;
                    double worstGcMs = 0;
                    gcRecorder = Recorder.Get("GC.Collect");
                    if (gcRecorder.isValid)
                    {
                        gcRecorder.FilterToCurrentThread();
                        gcRecorder.enabled = true;
                    }
                    if (incrementalBaseline)
                    {
                        incrementalRecorder = Recorder.Get("GarbageCollector.CollectIncremental");
                        if (incrementalRecorder.isValid) incrementalRecorder.enabled = true;
                    }

                    for (int frame = 0; frame < measuredFrames; frame++)
                    {
                        if (burstAndCleanup && frame == measuredFrames / 2)
                        {
                            Array.Clear(retained, 0, retained.Length);
                            double cleanupStart = Time.realtimeSinceStartupAsDouble;
                            if (schedulerOn) yield return scheduler.CleanupAfterSceneUnload();
                            else yield return Resources.UnloadUnusedAssets();
                            cleanupMs = (Time.realtimeSinceStartupAsDouble - cleanupStart) * 1000.0;
                        }

                        AllocateFrame(frame);
                        yield return null;
                        double frameMs = Time.unscaledDeltaTime * 1000.0;
                        frameTimesMs[frame] = frameMs;
                        totalMs += frameMs;
                        if (frameMs > 33.33) framesOver33Ms++;
                        if (frameMs > 50.0) framesOver50Ms++;
                        if (frameMs > 100.0) framesOver100Ms++;
                        long usedBytes = Profiler.GetMonoUsedSizeLong();
                        long reservedBytes = Profiler.GetMonoHeapSizeLong();
                        long unityReservedBytes = Profiler.GetTotalReservedMemoryLong();
                        if (usedBytes > peakManagedBytes) peakManagedBytes = usedBytes;
                        if (reservedBytes > peakReservedBytes) peakReservedBytes = reservedBytes;
                        if (unityReservedBytes > peakUnityReservedBytes) peakUnityReservedBytes = unityReservedBytes;
                        if (gcRecorder.isValid)
                        {
                            gcEvents += gcRecorder.sampleBlockCount;
                            double frameGcMs = gcRecorder.elapsedNanoseconds / 1000000.0;
                            gcTimeMs += frameGcMs;
                            if (frameGcMs > worstGcMs) worstGcMs = frameGcMs;
                        }
                        if (incrementalRecorder != null && incrementalRecorder.isValid)
                        {
                            gcEvents += incrementalRecorder.sampleBlockCount;
                            double frameGcMs = incrementalRecorder.elapsedNanoseconds / 1000000.0;
                            gcTimeMs += frameGcMs;
                            if (frameGcMs > worstGcMs) worstGcMs = frameGcMs;
                        }
                    }

                    if (gcRecorder.isValid) gcRecorder.enabled = false;
                    if (incrementalRecorder != null && incrementalRecorder.isValid) incrementalRecorder.enabled = false;
                    Array.Sort(frameTimesMs);

                    Result result = new Result
                    {
                        SchedulerOn = schedulerOn,
                        AverageMs = totalMs / measuredFrames,
                        P95Ms = Percentile(frameTimesMs, 0.95),
                        P99Ms = Percentile(frameTimesMs, 0.99),
                        WorstMs = frameTimesMs[measuredFrames - 1],
                        PeakManagedBytes = peakManagedBytes,
                        PeakReservedBytes = peakReservedBytes,
                        PeakUnityReservedBytes = peakUnityReservedBytes,
                        CleanupMs = cleanupMs,
                        FramesOver33Ms = framesOver33Ms,
                        FramesOver50Ms = framesOver50Ms,
                        FramesOver100Ms = framesOver100Ms,
                        SchedulerCollections = scheduler.CollectionCount - collectionsBefore,
                        AutomaticCollections = scheduler.AutomaticCollectionCount - automaticBefore,
                        IdleCollections = scheduler.IdleCollectionCount - idleBefore,
                        GcMarkerAvailable = gcRecorder.isValid || (incrementalRecorder != null && incrementalRecorder.isValid),
                        GcEvents = gcEvents,
                        GcTimeMs = gcTimeMs,
                        WorstGcMs = worstGcMs,
                        LastSchedulerGcMs = scheduler.LastDurationMs,
                        EffectiveBudgetMb = scheduler.EffectiveBudgetMb
                    };

                    Debug.Log(FormatResult(run + 1, result), this);
                    Array.Clear(retained, 0, retained.Length);
                    retained = null;
                    GC.Collect();
                    yield return null;
                    yield return null;
                }

            }
            finally
            {
                if (gcRecorder != null && gcRecorder.isValid) gcRecorder.enabled = false;
                if (incrementalRecorder != null && incrementalRecorder.isValid) incrementalRecorder.enabled = false;
                retained = null;
                scheduler.enabled = originalSchedulerEnabled;
                QualitySettings.vSyncCount = originalVSync;
                Application.targetFrameRate = originalTargetFrameRate;
                running = false;
            }
        }

        private double Percentile(double[] sortedValues, double fraction)
        {
            int index = (int)Math.Ceiling(sortedValues.Length * fraction) - 1;
            return sortedValues[Math.Max(0, index)];
        }

        private string ModeName(bool schedulerOn)
        {
            return schedulerOn ? "scheduler ON" : incrementalBaseline ? "incremental GC" : "automatic GC";
        }

        private string FormatResult(int run, Result result)
        {
            return $"GC run {run} ({ModeName(result.SchedulerOn)}, {(burstAndCleanup ? "burst/cleanup" : "continuous")}, {measuredFrames} frames): " +
                $"load {(long)allocationsPerFrame * bytesPerAllocation * measuredFrames / 1048576.0:F0} MB, target {targetFrameRate} FPS, " +
                $"average {result.AverageMs:F2} ms, p95 {result.P95Ms:F2} ms, p99 {result.P99Ms:F2} ms, worst {result.WorstMs:F2} ms, " +
                $">33/50/100 ms frames {result.FramesOver33Ms}/{result.FramesOver50Ms}/{result.FramesOver100Ms}, " +
                $"peak managed {result.PeakManagedBytes / 1048576.0:F1} MB, heap reserved {result.PeakReservedBytes / 1048576.0:F1} MB, Unity reserved {result.PeakUnityReservedBytes / 1048576.0:F1} MB, " +
                $"cleanup {result.CleanupMs:F2} ms, scheduler collections {result.SchedulerCollections} " +
                $"(threshold {result.AutomaticCollections}, idle {result.IdleCollections}, last {result.LastSchedulerGcMs:F2} ms, budget {result.EffectiveBudgetMb:F0} MB), " +
                $"GC marker {(result.GcMarkerAvailable ? $"{result.GcEvents} events / {result.GcTimeMs:F2} ms total / {result.WorstGcMs:F2} ms worst" : "unavailable")}.";
        }

        private void AllocateFrame(int frame)
        {
            byte[][] batch = new byte[allocationsPerFrame][];
            for (int i = 0; i < batch.Length; i++)
            {
                byte[] allocation = new byte[bytesPerAllocation];
                allocation[0] = (byte)i;
                allocation[allocation.Length - 1] = (byte)(i >> 8);
                batch[i] = allocation;
            }
            retained[frame % retained.Length] = batch;
        }
    }

    public struct Result
    {
        public bool SchedulerOn;
        public double AverageMs;
        public double P95Ms;
        public double P99Ms;
        public double WorstMs;
        public long PeakManagedBytes;
        public long PeakReservedBytes;
        public long PeakUnityReservedBytes;
        public double CleanupMs;
        public int FramesOver33Ms;
        public int FramesOver50Ms;
        public int FramesOver100Ms;
        public int SchedulerCollections;
        public int AutomaticCollections;
        public int IdleCollections;
        public bool GcMarkerAvailable;
        public int GcEvents;
        public double GcTimeMs;
        public double WorstGcMs;
        public double LastSchedulerGcMs;
        public double EffectiveBudgetMb;
    }
}

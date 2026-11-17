using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace DreamfireGarbageCollector.Scripts
{
    [DefaultExecutionOrder(-10000)]
    public sealed class ManagedGcScheduler : MonoBehaviour
    {
        public static ManagedGcScheduler Instance { get; private set; }

        [Header("Tune per target platform")]
        [SerializeField, Min(64)] int managedBudgetMb = 512;
        [SerializeField, Min(0.1f)] float sampleIntervalSeconds = 1f;
        [SerializeField, Range(0.5f, 0.95f)] float rearmAtBudgetFraction = 0.75f;
        [SerializeField, Min(1f)] float minCollectionSpacingSeconds = 10f;
        [SerializeField, Min(1)] int maxManagedBudgetMb = 1024;
        [SerializeField, Min(1f)] float ineffectiveRetrySeconds = 120f;
        [SerializeField, Min(1f)] float idleSeconds = 300f;
        [SerializeField, Min(1f)] float idleCollectionIntervalSeconds = 120f;

        public long LastBeforeBytes { get; private set; }
        public long LastAfterBytes { get; private set; }
        public double LastDurationMs { get; private set; }
        public int CollectionCount { get; private set; }
        public int AutomaticCollectionCount { get; private set; }
        public int IdleCollectionCount { get; private set; }
        public double EffectiveBudgetMb { get; private set; }

        private bool _ownsGcMode = false;
        private bool _budgetCollectionArmed = true;
        private bool _warnedOverBudget = false;
        private double _nextSampleTime = 0.0f;
        private double _lastCollectionTime = double.NegativeInfinity;
        private double _nextAutomaticCollectionTime;
        private double _lastInputTime;
        private double _lastIdleCollectionTime;
        private readonly Queue<double> _automaticCollectionTimes = new Queue<double>(5);

        private void Awake()
        {
            if(Instance != null && Instance != this)
            {
                Debug.LogError("Only one ManagedGcScheduler may be active.", this);
                enabled = false;
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable()
        {
            if (Instance != this || Application.isEditor || GarbageCollector.isIncremental) return;

            if (GarbageCollector.GCMode != GarbageCollector.Mode.Enabled)
            {
                Debug.LogError("GC mode is already controlled by another system.", this);
                enabled = false;
                return;
            }

            GarbageCollector.GCMode = GarbageCollector.Mode.Manual;
            _ownsGcMode = true;
            EffectiveBudgetMb = managedBudgetMb;
            _budgetCollectionArmed = true;
            _nextAutomaticCollectionTime = 0;
            _nextSampleTime = 0;
            _automaticCollectionTimes.Clear();
            _lastInputTime = Time.realtimeSinceStartupAsDouble;
            _lastIdleCollectionTime = _lastInputTime;
            InputSystem.onEvent += OnInputEvent;
        }

        private void OnDisable()
        {
            if (!_ownsGcMode) return;
            InputSystem.onEvent -= OnInputEvent;
            if (GarbageCollector.GCMode == GarbageCollector.Mode.Manual) GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
            _ownsGcMode = false;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!_ownsGcMode) return;

            if (GarbageCollector.GCMode != GarbageCollector.Mode.Manual)
            {
                Debug.LogError("GC mode changed externally; scheduler is stopping.", this);
                enabled = false;
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextSampleTime) return;
            _nextSampleTime = now + sampleIntervalSeconds;

            long usedBytes = Profiler.GetMonoUsedSizeLong();
            long rearmBytes = (long)(BudgetBytes() * rearmAtBudgetFraction);

            if (usedBytes <= rearmBytes)
            {
                _budgetCollectionArmed = true;
                _warnedOverBudget = false;
            }

            if (now - _lastInputTime >= idleSeconds && now - _lastIdleCollectionTime >= idleCollectionIntervalSeconds && usedBytes >= BudgetBytes() / 2)
            {
                CollectNow(compacting: true);
                IdleCollectionCount++;
                _lastIdleCollectionTime = now;
                return;
            }

            if (usedBytes >= BudgetBytes() && (_budgetCollectionArmed || now >= _nextAutomaticCollectionTime) && now - _lastCollectionTime >= minCollectionSpacingSeconds)
            {
                _budgetCollectionArmed = false;
                CollectNow();
                AutomaticCollectionCount++;
                _automaticCollectionTimes.Enqueue(now);
                while (_automaticCollectionTimes.Count > 5) _automaticCollectionTimes.Dequeue();
                if (_automaticCollectionTimes.Count == 5 && now - _automaticCollectionTimes.Peek() < 480.0)
                {
                    EffectiveBudgetMb = Math.Min(Math.Max(maxManagedBudgetMb, managedBudgetMb), EffectiveBudgetMb * 1.1);
                    _automaticCollectionTimes.Clear();
                }

                _budgetCollectionArmed = LastAfterBytes < BudgetBytes();
                _nextAutomaticCollectionTime = now + (LastAfterBytes >= BudgetBytes() || LastBeforeBytes - LastAfterBytes < 1024 * 1024 ? ineffectiveRetrySeconds : minCollectionSpacingSeconds);
                if (LastAfterBytes >= BudgetBytes() && !_warnedOverBudget)
                {
                    Debug.LogWarning($"Managed memory remains above budget after GC: " + $"{LastAfterBytes / (1024 * 1024)} MB / {EffectiveBudgetMb:F0} MB.", this);
                    _warnedOverBudget = true;
                }
            }
        }

        public void CollectNow()
        {
            CollectNow(compacting: false);
        }

        private void CollectNow(bool compacting)
        {
            if (!_ownsGcMode) return;
            LastBeforeBytes = Profiler.GetMonoUsedSizeLong();
            double start = Time.realtimeSinceStartupAsDouble;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: compacting);
            double end = Time.realtimeSinceStartupAsDouble;
            LastAfterBytes = Profiler.GetMonoUsedSizeLong();
            LastDurationMs = (end - start) * 1000.0;
            _lastCollectionTime = end;
            CollectionCount++;
        }

        public IEnumerator CleanupAfterSceneUnload()
        {
            yield return Resources.UnloadUnusedAssets();
            CollectNow();
        }

        private void OnInputEvent(InputEventPtr eventPtr, InputDevice device)
        {
            NotifyActivity();
        }

        public void NotifyActivity()
        {
            _lastInputTime = Time.realtimeSinceStartupAsDouble;
        }

        private long BudgetBytes() => (long)(EffectiveBudgetMb * 1024 * 1024);
    }
}

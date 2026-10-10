using System;

namespace Gem.UI;

/// <summary>
/// Three-phase cinematic reassembly states for the reactor core:
/// Phase 1: Halves remain split with open molten rift while active shards accelerate magnetically and annihilate.
/// Dramatic Pause: 400ms pause with halves held open at 18px and molten rift humming with tension.
/// Phase 2: Core halves slam shut with hydraulic spring overshoot, shockwave ring and white seam spark.
/// </summary>
public enum ReassemblyPhase : byte
{
    None = 0,
    Phase1_Absorption = 1,
    DramaticPause = 2,
    Phase2_Slam = 3
}

/// <summary>
/// State machine orchestrating the Arc Reactor Overdrive Shatter lifecycle:
/// Continuous bass accumulation, overload charge arc, timer refresh cap,
/// and 5-step Hyper-Slam cinematic reassembly choreography.
/// </summary>
public sealed class OverdriveStateMachine
{
    private readonly Random _rand = new();

    public bool EnableShatterOverdrive { get; set; } = true;
    public bool EnableFullscreenOverdrive { get; set; } = true;
    public double OverdriveThreshold { get; set; } = 0.85;
    public double OverdriveDuration { get; set; } = 10.0;
    public double OverdriveCooldown { get; set; } = 10.0;

    public bool IsOverdriveActive { get; set; } = false;
    public double OverdriveProgress { get; set; } = 0.0;
    public double OverdriveTimer { get; set; } = 0.0;
    public double CooldownTimer { get; set; } = 0.0;
    public double SmoothedCharge { get; set; } = 0.0;

    public bool IsReassembling { get; set; } = false;
    public ReassemblyPhase ReassemblyPhase { get; set; } = ReassemblyPhase.None;
    public double ReassemblyPhaseTimer { get; set; } = 0.0;

    public double SparkFlash { get; set; } = 0.0;
    public double WhiteoutIntensity { get; set; } = 0.0;
    public double ShockwaveRadius { get; set; } = 80.0;
    public double ShockwaveOpacity { get; set; } = 0.0;
    public double DetonationTitleBurstTimer { get; set; } = 0.0;

    public double ShakeOffsetX { get; set; } = 0.0;
    public double ShakeOffsetY { get; set; } = 0.0;

    private double _bassEnergyAccumulator = 0.0;
    private double _kickDebounceTimer = 0.0;
    private bool _wasOverdriveActive = false;

    public event Action<bool, double, float>? OnOverdriveFrame;

    public void TriggerBassFrame(float subBassEnergy, float beatIntensity, double dt, out bool shouldExplodeShards)
    {
        shouldExplodeShards = false;

        // 1. Update cooldown timer
        if (CooldownTimer > 0.0)
        {
            CooldownTimer = Math.Max(0.0, CooldownTimer - dt);
        }

        if (_kickDebounceTimer > 0.0)
        {
            _kickDebounceTimer = Math.Max(0.0, _kickDebounceTimer - dt);
        }

        // 2. Update reassembly state machine
        if (IsReassembling)
        {
            if (ReassemblyPhase == ReassemblyPhase.DramaticPause)
            {
                ReassemblyPhaseTimer -= dt;
                if (ReassemblyPhaseTimer <= 0.0)
                {
                    ReassemblyPhase = ReassemblyPhase.Phase2_Slam;
                    ReassemblyPhaseTimer = 0.0;
                    SparkFlash = 0.0;
                    ShockwaveRadius = 80.0;
                    ShockwaveOpacity = 0.0;
                    WhiteoutIntensity = 0.0;
                    CooldownTimer = OverdriveCooldown;
                }
            }
            else if (ReassemblyPhase == ReassemblyPhase.Phase2_Slam)
            {
                ReassemblyPhaseTimer += dt;
                double t = ReassemblyPhaseTimer;

                // Step 3: Core Whiteout (60-80 ms plasma flash in impact window t in [0.20, 0.28])
                if (t >= 0.20 && t < 0.28)
                {
                    double wT = (t - 0.20) / 0.08;
                    WhiteoutIntensity = Math.Max(0.0, 1.0 - wT);
                }
                else
                {
                    WhiteoutIntensity = 0.0;
                }

                // Step 4: Shockwave Blast (expanding radius 80 -> 370 px, opacity 1.0 -> 0.0 over 300 ms for t in [0.20, 0.50])
                if (t >= 0.20)
                {
                    double shockT = Math.Clamp((t - 0.20) / 0.30, 0.0, 1.0);
                    ShockwaveRadius = 80.0 + shockT * 290.0;
                    ShockwaveOpacity = Math.Max(0.0, 1.0 - shockT);
                }
                else
                {
                    ShockwaveRadius = 80.0;
                    ShockwaveOpacity = 0.0;
                }

                if (ReassemblyPhaseTimer >= 0.50)
                {
                    ReassemblyPhase = ReassemblyPhase.None;
                    IsReassembling = false;
                    ShockwaveOpacity = 0.0;
                    WhiteoutIntensity = 0.0;
                    OverdriveProgress = 0.0;
                }
            }
        }

        if (ReassemblyPhase == ReassemblyPhase.Phase2_Slam && ReassemblyPhaseTimer >= 0.20 && ReassemblyPhaseTimer < 0.35)
        {
            SparkFlash = Math.Max(0.0, 1.0 - (ReassemblyPhaseTimer - 0.20) / 0.15);
        }
        else if (SparkFlash > 0.0)
        {
            SparkFlash = Math.Max(0.0, SparkFlash - dt * 4.0);
        }

        if (DetonationTitleBurstTimer > 0.0)
        {
            DetonationTitleBurstTimer = Math.Max(0.0, DetonationTitleBurstTimer - dt);
        }

        if (!EnableShatterOverdrive && !EnableFullscreenOverdrive)
        {
            IsOverdriveActive = false;
            IsReassembling = false;
            ReassemblyPhase = ReassemblyPhase.None;
            _bassEnergyAccumulator = 0.0;
            OverdriveProgress = Math.Max(0.0, OverdriveProgress - dt * 4.0);
            OnOverdriveFrame?.Invoke(false, OverdriveProgress, beatIntensity);
            return;
        }

        float triggerThreshold = (float)Math.Clamp(OverdriveThreshold, 0.40, 1.50);
        float inputGate = 0.45f * (triggerThreshold / 0.85f);
        float combined = subBassEnergy * 0.6f + beatIntensity * 0.4f;
        double maxDuration = OverdriveDuration > 0.0 ? OverdriveDuration : 10.0;

        if (CooldownTimer > 0.0)
        {
            _bassEnergyAccumulator = 0.0;
            IsOverdriveActive = false;
        }
        else
        {
            if (combined > inputGate)
            {
                _bassEnergyAccumulator += (combined - inputGate * 0.75f) * dt * 0.40;
            }
            else
            {
                _bassEnergyAccumulator -= dt * 0.45;
            }

            _bassEnergyAccumulator = Math.Clamp(_bassEnergyAccumulator, 0.0, 2.0);

            if (!IsOverdriveActive && !IsReassembling && _bassEnergyAccumulator >= triggerThreshold)
            {
                IsOverdriveActive = true;
                OverdriveTimer = maxDuration;
                IsReassembling = false;
                ReassemblyPhase = ReassemblyPhase.None;
                ReassemblyPhaseTimer = 0.0;
                SparkFlash = 0.0;
                DetonationTitleBurstTimer = 2.0;
                shouldExplodeShards = true;
            }
            else if (IsOverdriveActive)
            {
                // Strict Timer Refresh Cap (Refresh, NOT Accumulation)
                if (combined > inputGate * 1.15f && _kickDebounceTimer <= 0.0)
                {
                    _kickDebounceTimer = 0.25;
                    if (OverdriveTimer < maxDuration)
                    {
                        OverdriveTimer = maxDuration;
                    }
                }

                OverdriveTimer = Math.Min(OverdriveTimer, maxDuration);
                OverdriveTimer -= dt;

                if (OverdriveTimer <= 0.0)
                {
                    IsOverdriveActive = false;
                    IsReassembling = true;
                    ReassemblyPhase = ReassemblyPhase.Phase1_Absorption;
                    ReassemblyPhaseTimer = 1.8;
                }
            }
        }

        // Update OverdriveProgress
        if (IsOverdriveActive)
        {
            double targetProgress = 1.0;
            OverdriveProgress += (targetProgress - OverdriveProgress) * Math.Clamp(dt * 8.0, 0.0, 1.0);
        }
        else if (IsReassembling)
        {
            if (ReassemblyPhase == ReassemblyPhase.Phase1_Absorption || ReassemblyPhase == ReassemblyPhase.DramaticPause)
            {
                OverdriveProgress = 1.0;
            }
            else if (ReassemblyPhase == ReassemblyPhase.Phase2_Slam)
            {
                if (ReassemblyPhaseTimer < 0.20)
                {
                    OverdriveProgress = 1.0;
                }
                else
                {
                    double coolT = Math.Clamp((ReassemblyPhaseTimer - 0.20) / 0.30, 0.0, 1.0);
                    OverdriveProgress = 1.0 - coolT;
                }
            }
        }
        else
        {
            OverdriveProgress = Math.Max(0.0, OverdriveProgress - dt * 6.0);
        }

        // Cooldown activation check when reassembly or overdrive ends
        if (_wasOverdriveActive && !IsOverdriveActive && !IsReassembling && OverdriveProgress <= 0.08)
        {
            _wasOverdriveActive = false;
            if (CooldownTimer <= 0.0)
            {
                CooldownTimer = OverdriveCooldown;
            }
        }

        if (IsOverdriveActive)
        {
            _wasOverdriveActive = true;
        }

        // Update charge ratio & screen jitter
        double rawCharge = (CooldownTimer > 0.0 || IsReassembling) ? 0.0 : Math.Clamp(_bassEnergyAccumulator / triggerThreshold, 0.0, 1.0);
        if (IsOverdriveActive) rawCharge = 1.0;
        SmoothedCharge += (rawCharge - SmoothedCharge) * Math.Clamp(dt * 4.0, 0.0, 1.0);

        if (SmoothedCharge > 0.80)
        {
            double jitterRatio = (SmoothedCharge - 0.80) / 0.20;
            double maxShake = 3.2 * jitterRatio;
            ShakeOffsetX = (_rand.NextDouble() - 0.5) * maxShake;
            ShakeOffsetY = (_rand.NextDouble() - 0.5) * maxShake;
        }
        else
        {
            ShakeOffsetX = 0.0;
            ShakeOffsetY = 0.0;
        }

        OnOverdriveFrame?.Invoke(IsOverdriveActive, OverdriveProgress, beatIntensity);
    }

    /// <summary>
    /// Calculates horizontal split distance of core halves in pixels based on overdrive/reassembly kinematics.
    /// Choreography:
    /// - Overdrive active: OverdriveProgress * 18.0 px
    /// - Phase 1 (Absorption) &amp; Dramatic Pause: held wide at 18.0 px
    /// - Phase 2 (Hyper Slam):
    ///   * Step 1: Anticipation / Inhale (120 ms, t in [0.0, 0.12]): widens further from 18px to 22px
    ///   * Step 2: Hyper Slam (80 ms, t in [0.12, 0.20]): EaseInExpo p^4 slam inward from 22px to 0px at t = 0.20
    ///   * Step 5: Heavy Mechanical Settle (300 ms, t in [0.20, 0.50]): damped spring oscillations settling rock-solid
    /// </summary>
    public double CalculateSplitDistance()
    {
        if (IsOverdriveActive)
        {
            return OverdriveProgress * 18.0;
        }
        else if (IsReassembling)
        {
            if (ReassemblyPhase == ReassemblyPhase.Phase1_Absorption || ReassemblyPhase == ReassemblyPhase.DramaticPause)
            {
                return 18.0;
            }
            else if (ReassemblyPhase == ReassemblyPhase.Phase2_Slam)
            {
                double t = ReassemblyPhaseTimer;
                if (t < 0.12)
                {
                    return 18.0 + 4.0 * Math.Sin((t / 0.12) * Math.PI / 2.0);
                }
                else if (t < 0.20)
                {
                    double p = (t - 0.12) / 0.08;
                    return 22.0 * (1.0 - p * p * p * p);
                }
                else
                {
                    double tau = t - 0.20;
                    return Math.Max(0.0, 3.6 * Math.Exp(-14.0 * tau) * Math.Cos(45.0 * tau));
                }
            }
        }
        return 0.0;
    }
}

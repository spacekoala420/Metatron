using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using EVE.ISXEVE;
using Metatron.Core;
using LavishScriptAPI;
using Metatron.Core.Config;
using Metatron.Core.Interfaces;
using System.Speech.Synthesis.TtsEngine;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace Metatron.ActionModules
{
    // ReSharper disable CompareOfFloatsByEqualityOperator
    // ReSharper disable PossibleMultipleEnumeration
    // ReSharper disable ConvertToConstant.Local
    internal sealed class NonOffensive : ModuleBase
    {
        //bool canChangeTarget = false;
        private DateTime? _nextShortCycleDeactivation;
        private double _capacitor;

        private readonly RandomWaitObject _randomWait = new RandomWaitObject("NonOffensive");

        private readonly IMeCache _meCache;

        private readonly IMiningConfiguration _miningConfiguration;
        private readonly IDefensiveConfiguration _defensiveConfiguration;
        private readonly ISalvageConfiguration _salvageConfiguration;

        private readonly IEntityProvider _entityProvider;
        private readonly ITargetQueue _targetQueue;

        private readonly IShip _ship;
        private readonly IDrones _drones;

        private readonly ITargeting _targeting;
        private readonly IMovement _movement;

        public NonOffensive(IMeCache meCache, IMiningConfiguration miningConfiguration, IDefensiveConfiguration defensiveConfiguration, IEntityProvider entityProvider,
            ITargetQueue targetQueue, IShip ship, IDrones drones, ITargeting targeting, IMovement movement, ISalvageConfiguration salvageConfiguration)
        {
            _meCache = meCache;
            _miningConfiguration = miningConfiguration;
            _defensiveConfiguration = defensiveConfiguration;
            _salvageConfiguration = salvageConfiguration;
            _entityProvider = entityProvider;
            _targetQueue = targetQueue;
            _ship = ship;
            _drones = drones;
            _targeting = targeting;
            _movement = movement;

            ModuleManager.ModulesToPulse.Add(this);
            PulseFrequency = 1;
            ModuleName = "NonOffensive";

            _randomWait.AddWait(new KeyValuePair<int, int>(16, 30), 1);
            _randomWait.AddWait(new KeyValuePair<int, int>(6, 15), 3);
            _randomWait.AddWait(new KeyValuePair<int, int>(3, 5), 6);
            _randomWait.AddWait(new KeyValuePair<int, int>(1, 2), 10);
        }

        public override void Pulse()
        {
            var methodName = "Pulse";
            LogTrace(methodName);

            if (!ShouldPulse())
                return;

            if (!_meCache.InSpace || _meCache.InStation)
                return;

            if (_movement.IsMoving && _movement.MovementType == MovementTypes.Warp)
            {
                //Make sure all modules are off
                _ship.DeactivateModuleList(_ship.MiningLaserModules, true);
                _ship.DeactivateModuleList(_ship.SalvagerModules, true);

                if (Core.Metatron.Config.MiningConfig.UseMiningDrones && _drones.DronesInSpace > 0)
                {
                    _drones.RecallAllDrones(true);
                }
                return;
            }

            if (_randomWait.ShouldWait()) return;

            var activeTargetId = _meCache.ActiveTargetId;
            if (activeTargetId <= 0) return;

            StartPulseProfiling();
            _capacitor = _meCache.Ship.Capacitor;

            var activeTarget = _entityProvider.EntityWrappersById[activeTargetId];
            var activeQueueTarget = _targeting.GetActiveQueueTarget();

            if (activeQueueTarget != null && !_targeting.WasTargetChangedThisFrame)
            {
                switch (activeQueueTarget.Type)
                {
                    case TargetTypes.Mine:
                        //If it's not in range, approach
                        //Otherwise mine
                        if (_entityProvider.EntityWrappersById[activeQueueTarget.Id].Distance > _ship.MaximumMiningRange &&
                            (_meCache.ToEntity.Approaching == null || _meCache.ToEntity.Approaching.ID != activeQueueTarget.Id))
                        {
                            //Dequeue it, shouldn't be queued any more
                            _targetQueue.DequeueTarget(activeQueueTarget.Id);
                            //Unlock it
                            _targeting.UnlockTarget(activeTarget);
                        }
                        break;
                    case TargetTypes.LootSalvage:
                        TractorTarget(activeQueueTarget);
                        break;
                }
            }

            MineTargets();
            ManageIdleSalvageModules();
            EndPulseProfiling();
        }

        /// <summary>
        /// Mine queued mining targets by order of priority.
        /// </summary>
        private void MineTargets()
        {
            var methodName = "MineTargets";
            LogTrace(methodName);

            var miningTargets = GetMiningTargets();

            if (!miningTargets.Any()) return;

            UseMiningDrones(miningTargets);

            UseMiningLasers(miningTargets);
        }

        /// <summary>
        /// Helper: Check if entity is a wreck (can be salvaged)
        /// </summary>
        private bool IsWreck(IEntityWrapper entity)
        {
            return entity.GroupID == (int)GroupIDs.Wreck;
        }

        /// <summary>
        /// Helper: Check if entity is a cargo container (can only be looted, not salvaged)
        /// </summary>
        private bool IsCargoContainer(IEntityWrapper entity)
        {
            return entity.GroupID == (int)GroupIDs.CargoContainer;
        }

        /// <summary>
        /// Helper: Check if entity needs looting
        /// </summary>
        private bool NeedsLooting(IEntityWrapper entity)
        {
            if (IsWreck(entity))
            {
                // Wrecks have the IsWreckEmpty property
                return !entity.ToEntity.IsWreckEmpty;
            }
            else if (IsCargoContainer(entity))
            {
                // Containers don't have IsWreckEmpty, always try to loot them
                return true;
            }
            return false;
        }

        /// <summary>
        /// Manage salvage modules across ALL locked targets.
        /// Activates tractors on distant targets, salvagers on wrecks only, respects bonused ship rules.
        /// Does NOT change active target - works with whatever targeting system provides.
        /// </summary>
        private void ManageIdleSalvageModules()
        {
            var methodName = "ManageIdleSalvageModules";
            LogTrace(methodName);

            var salvageTargets = GetSalvageTargets();
            if (!salvageTargets.Any()) return;

            // PHASE 1: Cleanup - deactivate modules on invalid/despawned targets
            foreach (var tractor in _ship.TractorBeamModules.Where(t => t.IsActive))
            {
                if (!_entityProvider.EntityWrappersById.ContainsKey(tractor.TargetID))
                {
                    LogMessage(methodName, LogSeverityTypes.Debug, "Deactivating tractor - target despawned.");
                    tractor.Deactivate();
                }
            }

            foreach (var salvager in _ship.SalvagerModules.Where(s => s.IsActive))
            {
                if (!_entityProvider.EntityWrappersById.ContainsKey(salvager.TargetID))
                {
                    LogMessage(methodName, LogSeverityTypes.Debug, "Deactivating salvager - target despawned.");
                    salvager.Deactivate();
                }
            }

            // PHASE 2: Deactivate tractors on targets now in loot range
            foreach (var tractor in _ship.TractorBeamModules.Where(t => t.IsActive))
            {
                if (_entityProvider.EntityWrappersById.ContainsKey(tractor.TargetID))
                {
                    var target = _entityProvider.EntityWrappersById[tractor.TargetID];
                    if (target.Distance <= (int)Ranges.LootActivate)
                    {
                        LogMessage(methodName, LogSeverityTypes.Debug, "Deactivating tractor on \"{0}\" - in loot range ({1:F0}m).",
                            target.Name, target.Distance);
                        tractor.Deactivate();
                    }
                }
            }

            // PHASE 3: Activate tractors on distant locked targets
            var availableTractors = _ship.TractorBeamModules.Where(t => !t.IsActive).ToList();
            if (availableTractors.Any())
            {
                // Find locked targets that are far away and don't have a tractor yet
                var targetsNeedingTractor = salvageTargets
                    .Where(t => t.IsLockedTarget
                        && t.Distance > (int)Ranges.LootActivate
                        && !_ship.TractorBeamModules.Any(tr => tr.IsActive && tr.TargetID == t.ID))
                    .OrderBy(t => t.Distance)  // Furthest first
                    .ToList();

                // Activate one tractor per pulse to naturally distribute
                foreach (var tractor in availableTractors)
                {
                    var target = targetsNeedingTractor.FirstOrDefault(t => t.Distance <= tractor.OptimalRange.GetValueOrDefault(0));
                    if (target != null)
                    {
                        // Need to make this the active target to activate the module
                        if (_meCache.ActiveTargetId != target.ID && _targeting.CanChangeTarget && !_targeting.WasTargetChangedThisFrame)
                        {
                            LogMessage(methodName, LogSeverityTypes.Debug, "Changing to \"{0}\" to activate tractor.", target.Name);
                            _targeting.ChangeTargetTo(target, false);
                        }

                        if (_meCache.ActiveTargetId == target.ID)
                        {
                            LogMessage(methodName, LogSeverityTypes.Standard, "Activating tractor on \"{0}\" ({1:F0}m away).",
                                target.Name, target.Distance);
                            tractor.Activate();
                            targetsNeedingTractor.Remove(target);
                            break;  // Only activate one tractor per pulse
                        }
                    }
                }
            }

            // PHASE 4: Activate salvagers on WRECKS only (not cargo containers!)
            var availableSalvagers = _ship.SalvagerModules.Where(s => !s.IsActive).ToList();
            if (availableSalvagers.Any())
            {
                // Find locked WRECKS in salvager range that need salvaging
                var wrecksNeedingSalvage = salvageTargets
                    .Where(t => t.IsLockedTarget
                        && IsWreck(t)  // CRITICAL: Only wrecks can be salvaged!
                        && t.Distance <= 6000)  // Within salvager range
                    .OrderBy(t => t.Distance)
                    .ToList();

                // Determine how many salvagers to put on each wreck based on configuration
                if (_salvageConfiguration.DistributeSalvagers)
                {
                    // BONUSED MODE: 1 salvager per wreck, distribute across many wrecks
                    var wrecksWithoutSalvager = wrecksNeedingSalvage
                        .Where(w => !_ship.SalvagerModules.Any(s => s.IsActive && s.TargetID == w.ID))
                        .ToList();

                    // Activate one salvager per pulse on different wrecks
                    foreach (var salvager in availableSalvagers)
                    {
                        var wreck = wrecksWithoutSalvager.FirstOrDefault();
                        if (wreck != null)
                        {
                            // Need to make this the active target
                            if (_meCache.ActiveTargetId != wreck.ID && _targeting.CanChangeTarget && !_targeting.WasTargetChangedThisFrame)
                            {
                                LogMessage(methodName, LogSeverityTypes.Debug, "Changing to \"{0}\" to activate salvager.", wreck.Name);
                                _targeting.ChangeTargetTo(wreck, true);
                            }

                            if (_meCache.ActiveTargetId == wreck.ID)
                            {
                                LogMessage(methodName, LogSeverityTypes.Standard, "Activating salvager on wreck \"{0}\" ({1:F0}m away).",
                                    wreck.Name, wreck.Distance);
                                salvager.Click();
                                wrecksWithoutSalvager.Remove(wreck);
                                break;  // Only activate one salvager per pulse (spreads naturally)
                            }
                        }
                    }
                }
                else
                {
                    // NON-BONUSED MODE: Put all salvagers on the closest wreck
                    var targetWreck = wrecksNeedingSalvage.FirstOrDefault();
                    if (targetWreck != null)
                    {
                        // Change to this target if needed
                        if (_meCache.ActiveTargetId != targetWreck.ID && _targeting.CanChangeTarget && !_targeting.WasTargetChangedThisFrame)
                        {
                            LogMessage(methodName, LogSeverityTypes.Debug, "Changing to \"{0}\" to focus all salvagers.", targetWreck.Name);
                            _targeting.ChangeTargetTo(targetWreck, true);
                        }

                        if (_meCache.ActiveTargetId == targetWreck.ID)
                        {
                            // Activate all available salvagers on this wreck
                            foreach (var salvager in availableSalvagers)
                            {
                                salvager.Click();
                            }
                            LogMessage(methodName, LogSeverityTypes.Standard, "Activated {0} salvager(s) on wreck \"{1}\".",
                                availableSalvagers.Count, targetWreck.Name);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// OLD METHOD - REPLACED BY TractorTarget() handling everything on active target.
        /// Keeping for reference but not used anymore.
        /// </summary>
        private void UseTractorBeams_OLD()
        {
            var methodName = "UseTractorBeams";
            LogTrace(methodName);

            var tractorBeamModules = _ship.TractorBeamModules;
            if (tractorBeamModules.Count == 0) return;

            var salvageTargets = GetSalvageTargets();
            if (!salvageTargets.Any()) return;

            // First pass: Deactivate tractors on targets that are now in loot range or invalid
            foreach (var tractorBeam in tractorBeamModules.Where(m => m.IsActive))
            {
                var tractorTargetId = tractorBeam.TargetID;

                // Check if target still exists
                if (!_entityProvider.EntityWrappersById.ContainsKey(tractorTargetId))
                {
                    LogMessage(methodName, LogSeverityTypes.Debug, "Deactivating tractor beam - target no longer exists.");
                    tractorBeam.Deactivate();
                    continue;
                }

                var tractorTarget = _entityProvider.EntityWrappersById[tractorTargetId];

                // Deactivate if wreck is now in loot range (close enough to loot without tractor)
                if (tractorTarget.Distance <= (int)Ranges.LootActivate)
                {
                    LogMessage(methodName, LogSeverityTypes.Debug, "Deactivating tractor beam on \"{0}\" - now in loot range ({1:F0}m).",
                        tractorTarget.Name, tractorTarget.Distance);
                    tractorBeam.Deactivate();
                }
            }

            // Second pass: Activate available tractors on wrecks that need pulling
            // Find wrecks that need tractoring: locked, out of loot range, but within tractor range
            var wrecksNeedingTractor = salvageTargets
                .Where(w => w.IsLockedTarget
                    && w.Distance > (int)Ranges.LootActivate  // Too far to loot
                    && !tractorBeamModules.Any(t => t.IsActive && t.TargetID == w.ID))  // Not already being tractored
                .OrderBy(w => w.Distance)  // Closest first
                .ToList();

            if (!wrecksNeedingTractor.Any())
            {
                LogMessage(methodName, LogSeverityTypes.Debug, "No wrecks need tractoring.");
                return;
            }

            LogMessage(methodName, LogSeverityTypes.Debug, "{0} wreck(s) need tractoring, {1} tractor(s) available.",
                wrecksNeedingTractor.Count, tractorBeamModules.Count(t => !t.IsActive));

            // Activate available tractors
            foreach (var tractorBeam in tractorBeamModules.Where(t => !t.IsActive))
            {
                // Find a wreck that needs this tractor and is in range
                var wreckToTractor = wrecksNeedingTractor
                    .FirstOrDefault(w => w.Distance <= tractorBeam.OptimalRange.GetValueOrDefault(0));

                if (wreckToTractor == null)
                {
                    LogMessage(methodName, LogSeverityTypes.Debug, "No wreck in range for tractor beam.");
                    continue;
                }

                // Change active target if needed (required for module activation in EVE)
                if (_meCache.ActiveTargetId != wreckToTractor.ID)
                {
                    if (!_targeting.CanChangeTarget)
                    {
                        LogMessage(methodName, LogSeverityTypes.Debug, "Cannot change target to activate tractor.");
                        break;  // Can't activate any more this pulse
                    }

                    LogMessage(methodName, LogSeverityTypes.Debug, "Changing target to \"{0}\" for tractor activation.",
                        wreckToTractor.Name);
                    _targeting.ChangeTargetTo(wreckToTractor, false);
                }

                // Activate the tractor beam
                LogMessage(methodName, LogSeverityTypes.Standard, "Activating tractor beam on \"{0}\" ({1:F0}m away).",
                    wreckToTractor.Name, wreckToTractor.Distance);
                tractorBeam.Activate();

                // Remove this wreck from the list so we don't try to activate another tractor on it this pulse
                wrecksNeedingTractor.Remove(wreckToTractor);

                if (!wrecksNeedingTractor.Any()) break;  // No more wrecks need tractoring
            }
        }

        /// <summary>
        /// OLD METHOD - REPLACED BY TractorTarget() handling everything on active target.
        /// Keeping for reference but not used anymore.
        /// </summary>
        private void UseSalvagers_OLD()
        {
            var methodName = "UseSalvagers";
            LogTrace(methodName);

            var salvagerModules = _ship.SalvagerModules;
            if (salvagerModules.Count == 0) return;

            var salvageTargets = GetSalvageTargets();
            if (!salvageTargets.Any()) return;

            // First pass: Deactivate salvagers on invalid targets (salvaged/despawned wrecks)
            foreach (var salvager in salvagerModules.Where(s => s.IsActive))
            {
                var salvagerTargetId = salvager.TargetID;

                // Check if target still exists and is still in the salvage queue
                if (!_entityProvider.EntityWrappersById.ContainsKey(salvagerTargetId) ||
                    !salvageTargets.Any(t => t.ID == salvagerTargetId))
                {
                    LogMessage(methodName, LogSeverityTypes.Debug, "Deactivating salvager - target no longer valid.");
                    salvager.Deactivate();
                }
            }

            // Second pass: Activate available salvagers on wrecks that need salvaging
            var inactiveSalvagers = salvagerModules.Where(s => !s.IsActive).ToList();
            if (!inactiveSalvagers.Any())
            {
                LogMessage(methodName, LogSeverityTypes.Debug, "All salvagers are busy.");
                return;
            }

            // Find wrecks that need salvaging: locked and in range
            var wrecksNeedingSalvage = salvageTargets
                .Where(w => w.IsLockedTarget)
                .OrderBy(w => w.Distance)  // Closest first
                .ToList();

            if (!wrecksNeedingSalvage.Any())
            {
                LogMessage(methodName, LogSeverityTypes.Debug, "No locked wrecks available for salvaging.");
                return;
            }

            LogMessage(methodName, LogSeverityTypes.Debug, "{0} wreck(s) need salvaging, {1} salvager(s) available.",
                wrecksNeedingSalvage.Count, inactiveSalvagers.Count);

            // Determine strategy based on configuration
            if (_salvageConfiguration.DistributeSalvagers)
            {
                // DISTRIBUTED: Try to spread salvagers across different wrecks (limited by active target constraint)
                // Each pulse we activate one salvager on a different wreck (naturally distributes over time)
                foreach (var salvager in inactiveSalvagers)
                {
                    // Find a wreck that doesn't already have a salvager working on it (if possible)
                    var wreckToSalvage = wrecksNeedingSalvage
                        .Where(w => w.Distance <= salvager.OptimalRange.GetValueOrDefault(0))
                        .FirstOrDefault(w => !salvagerModules.Any(s => s.IsActive && s.TargetID == w.ID));

                    // If all wrecks already have salvagers, just pick the closest one in range
                    if (wreckToSalvage == null)
                    {
                        wreckToSalvage = wrecksNeedingSalvage
                            .FirstOrDefault(w => w.Distance <= salvager.OptimalRange.GetValueOrDefault(0));
                    }

                    if (wreckToSalvage == null)
                    {
                        LogMessage(methodName, LogSeverityTypes.Debug, "No wreck in range for salvager.");
                        continue;
                    }

                    // Change active target if needed
                    if (_meCache.ActiveTargetId != wreckToSalvage.ID)
                    {
                        if (!_targeting.CanChangeTarget || _targeting.WasTargetChangedThisFrame)
                        {
                            LogMessage(methodName, LogSeverityTypes.Debug, "Cannot change target to activate salvager.");
                            break;  // Can't activate any more this pulse
                        }

                        LogMessage(methodName, LogSeverityTypes.Debug, "Changing target to \"{0}\" for salvager activation.",
                            wreckToSalvage.Name);
                        _targeting.ChangeTargetTo(wreckToSalvage, true);
                    }

                    // Activate the salvager
                    LogMessage(methodName, LogSeverityTypes.Standard, "Activating salvager on \"{0}\" ({1:F0}m away).",
                        wreckToSalvage.Name, wreckToSalvage.Distance);
                    salvager.Click();

                    // In distributed mode, activate one salvager per pulse to spread across wrecks
                    break;
                }
            }
            else
            {
                // FOCUSED: Activate all salvagers on the same wreck (salvage it faster)
                // Find the closest wreck that is actually in salvager range
                var salvagerRange = inactiveSalvagers.Select(s => s.OptimalRange.GetValueOrDefault(0)).DefaultIfEmpty(0).Min();

                var targetWreck = wrecksNeedingSalvage
                    .Where(w => w.Distance <= salvagerRange)  // Must be in range!
                    .FirstOrDefault();

                if (targetWreck == null)
                {
                    LogMessage(methodName, LogSeverityTypes.Debug, "No wrecks in salvager range (~{0:F0}m). Waiting for tractors to pull wrecks closer.",
                        salvagerRange);
                    return;
                }

                // Get salvagers that can reach this target
                var salvagersInRange = inactiveSalvagers
                    .Where(s => targetWreck.Distance <= s.OptimalRange.GetValueOrDefault(0))
                    .ToList();

                // Change active target if needed
                if (_meCache.ActiveTargetId != targetWreck.ID)
                {
                    if (!_targeting.CanChangeTarget || _targeting.WasTargetChangedThisFrame)
                    {
                        LogMessage(methodName, LogSeverityTypes.Debug, "Cannot change target to activate salvagers.");
                        return;
                    }

                    LogMessage(methodName, LogSeverityTypes.Debug, "Changing target to \"{0}\" for salvager activation.",
                        targetWreck.Name);
                    _targeting.ChangeTargetTo(targetWreck, true);
                }

                // Activate all salvagers in range on the target
                foreach (var salvager in salvagersInRange)
                {
                    salvager.Click();
                }

                LogMessage(methodName, LogSeverityTypes.Standard, "Activated {0} salvager(s) on \"{1}\" ({2:F0}m away).",
                    salvagersInRange.Count, targetWreck.Name, targetWreck.Distance);
            }
        }

        /// <summary>
        /// Obtain a list of salvage targets (wrecks/containers), ordered by priority.
        /// </summary>
        /// <returns></returns>
        private ICollection<IEntityWrapper> GetSalvageTargets()
        {
            var methodName = "GetSalvageTargets";
            LogTrace(methodName);

            var salvageTargets = _targetQueue.Targets
                .Join(_entityProvider.EntityWrappers, queueTarget => queueTarget.Id, entity => entity.ID, (queueTarget, entity) => new { queueTarget, entity })
                .Where(pair => pair.queueTarget.Type == TargetTypes.LootSalvage)
                .OrderBy(pair => pair.queueTarget.Priority)
                .ThenByDescending(pair => pair.queueTarget.SubPriority)
                .ThenBy(pair => pair.entity.Distance)
                .Select(pair => pair.entity)
                .ToList();

            return salvageTargets;
        }

        /// <summary>
        /// Use all mining lasers on targets ordered by priority, optionally distributing them.
        /// </summary>
        /// <param name="miningTargets"></param>
        private void UseMiningLasers(ICollection<IEntityWrapper> miningTargets)
        {
            var methodName = "UseMiningLasers";
            LogTrace(methodName);

            var miningLasers = _ship.MiningLaserModules;
            var lowestMaximumLaserRange = GetLowestMaximumLaserRange();

            if (lowestMaximumLaserRange == 0)
            {
                LogMessage(methodName, LogSeverityTypes.Debug, "Error: All mining lasers had an invalid optimal range.");
                return;
            }

            var takeCount = _miningConfiguration.DistributeLasers ? miningLasers.Count : 1;

            var miningTargetsInRange = miningTargets
                .Where(target => target.Distance <= lowestMaximumLaserRange);

            var chosenTargets = miningTargetsInRange
                .Take(takeCount);

            LogMessage(methodName, LogSeverityTypes.Debug, "Targets in range: {0}, chosen targets: {1}",
                miningTargetsInRange.Count(), chosenTargets.Count());

            //var chosenTargets = miningTargets
            //    .Where(target => target.Distance <= lowestMaximumLaserRange)
            //    .Take(takeCount);

            if (!chosenTargets.Any())
            {
                LogMessage(methodName, LogSeverityTypes.Debug, "No mining targets were within the maximum range of {0}m.",
                    lowestMaximumLaserRange);
                return;
            }

            //If we're short cycling and need to cut lasers, deactivate lasers and return.
            if (_miningConfiguration.ShortCycle)
            {
                //Short cycling should do nothing or deactivate all lasers. If we deactivate lasers, there's nothing else to do this pulse, return early.
                var wereLasersDeactivated = ShortCycleLasers(miningLasers);
                if (wereLasersDeactivated) return;
            }

            //Distribute out my lasers
            var activeModuleCountByTargetId = new Dictionary<Int64, int>();
            var intendedModuleCountByTargetId = new Dictionary<Int64, int>();
            DetermineModuleCountsForTargets(chosenTargets, miningLasers, activeModuleCountByTargetId, intendedModuleCountByTargetId);

            //If there are no intended modules on my chosen targets, I'm done.
            if (intendedModuleCountByTargetId.Values.Sum() == 0) return;

            //Track which lasers to activate and activate them outside the loop.
            //This way, we don't accidentally activate laser A then rearm laser B in the same pulse. A human can't do that (easily).
            var miningLasersToActivateOnActiveTarget = new List<EVE.ISXEVE.Interfaces.IModule>();

            foreach (var miningLaser in miningLasers)
            {
                //If the laser's target is invalid, deactivate it.
                if (miningLaser.IsActive)
                {
                    if (!miningLaser.IsDeactivating && !miningTargets.Any(miningTarget => miningTarget.ID == miningLaser.TargetID))
                    {
                        LogMessage(methodName, LogSeverityTypes.Debug, "Deactivating module \"{0}\" ({1}) because its target is invalid.",
                            miningLaser.ToItem.Name, miningLaser.ToItem.GroupID);
                        miningLaser.Click();
                    }
                    continue;
                }

                //Pick a target for this laser
                var chosenTarget = GetBestTargetForMiningLaser(chosenTargets, activeModuleCountByTargetId, intendedModuleCountByTargetId);

                if (chosenTarget == null)
                {
                    //LogMessage(methodName, LogSeverityTypes.Debug, "Error: Unable to determine a target for mining laser \"{0}\" ({1}).");
                    continue;
                }

                LogMessage(methodName, LogSeverityTypes.Debug, "Chosen target for laser: \"{0}\" ({1})", chosenTarget.Name, chosenTarget.ID);

                //Do I need to re-arm this laser?
                var wasLaserRearmed = EnsureMiningLaserIsArmedForTarget(miningLaser, chosenTarget);
                if (wasLaserRearmed) return; // If a laser is re-armed we can do no other operation this pulse.

                //If the active target was changed this frame, the "active target" is no longer valid. Don't process any more.
                if (_targeting.WasTargetChangedThisFrame)
                {
                    LogMessage(methodName, LogSeverityTypes.Debug, "The active target was changed this pulse.");
                    continue;
                }

                //Do we have capacitor to activate the laser?
                var canSafelyActivate = CanSafelyActivateMiningLaser(miningLaser);
                if (!canSafelyActivate) continue;

                //Do I need to change target?
                if (_meCache.ActiveTargetId != chosenTarget.ID)
                {
                    //Only change target if we don't have ANY lasers we plan on activating on the current target
                    if (miningLasersToActivateOnActiveTarget.Count == 0 && chosenTarget.IsLockedTarget && _targeting.CanChangeTarget)
                    {
                        LogMessage(methodName, LogSeverityTypes.Debug, "Changing target to \"{0}\" ({1}) for module \"{2}\" ({3}).",
                            chosenTarget.Name, chosenTarget.ID, miningLaser.ToItem.Name, miningLaser.ToItem.ID);
                        _targeting.ChangeTargetTo(chosenTarget, true);
                    }

                    continue;
                }

                //It's armed correctly and we have its target active - activate it.
                miningLasersToActivateOnActiveTarget.Add(miningLaser);
                LogMessage(methodName, LogSeverityTypes.Debug, "We should activate mining laser \"{0}\" ({1}) on active target \"{2}\" ({3}).",
                    miningLaser.ToItem.Name, miningLaser.ID, _meCache.ActiveTarget.Name, _meCache.ActiveTargetId);
            }

            //why are we using lasers bakcwards?

            foreach (var miningLaser in miningLasersToActivateOnActiveTarget)
            {
                miningLaser.Click();

                //If I'm short cycling then update the next short cycle delay.
                //This will reset with the last laser activated, so as to err on the side of cap safety.
                if (Core.Metatron.Config.MiningConfig.ShortCycle)
                {
                    UpdateNextShortCycleDeactivation(miningLasers);
                }
            }
        }

        /// <summary>
        /// Determine the best of the given targets for the next mining laser.
        /// </summary>
        /// <param name="chosenTargets"></param>
        /// <param name="activeModuleCountByTargetId"></param>
        /// <param name="intendedModuleCountByTargetId"></param>
        /// <returns></returns>
        private IEntityWrapper GetBestTargetForMiningLaser(IEnumerable<IEntityWrapper> chosenTargets, IDictionary<long, int> activeModuleCountByTargetId,
                                                           IDictionary<long, int> intendedModuleCountByTargetId)
        {
            var methodName = "GetBestTargetForMiningLaser";
            LogTrace(methodName);

            IEntityWrapper chosenTarget = null;
            foreach (var target in chosenTargets)
            {
                var activeModuleCount = activeModuleCountByTargetId[target.ID];
                var intendedModuleCount = intendedModuleCountByTargetId[target.ID];

                var neededModules = intendedModuleCount - activeModuleCount;
                if (neededModules <= 0) continue;

                chosenTarget = target;
                activeModuleCountByTargetId[target.ID]++;
                break;
            }
            return chosenTarget;
        }

        /// <summary>
        /// Determine the counts of modules active against a target and intended for a target.
        /// </summary>
        /// <param name="chosenTargets"></param>
        /// <param name="miningLasers"></param>
        /// <param name="activeModuleCountByTargetId"></param>
        /// <param name="intendedModuleCountByTargetId"></param>
        private void DetermineModuleCountsForTargets(IEnumerable<IEntityWrapper> chosenTargets, ICollection<EVE.ISXEVE.Interfaces.IModule> miningLasers,
                                                     IDictionary<long, int> activeModuleCountByTargetId,
                                                     IDictionary<long, int> intendedModuleCountByTargetId)
        {
            var methodName = "DetermineModuleCountsForTargets";
            LogTrace(methodName);

            var temporaryTargetList = new List<IEntityWrapper>(chosenTargets);
            for (var index = 0; index < temporaryTargetList.Count; temporaryTargetList.RemoveAt(index))
            {
                var chosenTarget = temporaryTargetList[index];

                var countOnTarget = miningLasers.Count(module => module.IsActive && module.TargetID == chosenTarget.ID);
                //# lasers on a given target = ceiling(# lasers / # targets)
                //e.g. 4 lasers, 3 targets: laserson first target = ceiling(4/3) = 2, now have 2 lasers left, lasers on 2nd target = ceiling(2/2) = 1
                var intendedLaserCount = (int)Math.Ceiling((double)miningLasers.Count / temporaryTargetList.Count);

                activeModuleCountByTargetId.Add(chosenTarget.ID, countOnTarget);
                intendedModuleCountByTargetId.Add(chosenTarget.ID, intendedLaserCount);
            }
        }

        /// <summary>
        /// Determine whether or not we can activate a mining laser without going critically low on capacitor.
        /// </summary>
        /// <param name="miningLaser"></param>
        private bool CanSafelyActivateMiningLaser(EVE.ISXEVE.Interfaces.IModule miningLaser)
        {
            var methodName = "CanSafelyActivateMiningLaser";
            LogTrace(methodName);

            if (miningLaser.ActivationCost == null)
            {
                LogMessage(methodName, LogSeverityTypes.Debug, "Error: Module \"{0}\" ({1}) has an invalid activation cost.",
                    miningLaser.ToItem.Name, miningLaser.ID);
                return true;
            }

            var minimumCapacitor = CalculateMinimumCapacitor();

            LogMessage(methodName, LogSeverityTypes.Debug, "Capacitor: {0}, minimumCapacitor: {1}, activationCost: {2}",
                _capacitor, minimumCapacitor, miningLaser.ActivationCost.Value);

            var canSafelyActivateMiningLaser = _capacitor > minimumCapacitor + miningLaser.ActivationCost.Value;
            if (canSafelyActivateMiningLaser)
            {
                _capacitor -= miningLaser.ActivationCost.Value;
            }

            return canSafelyActivateMiningLaser;
        }

        /// <summary>
        /// Calculate the minimum capacitor amount we need for safe operation.
        /// </summary>
        /// <returns></returns>
        private double CalculateMinimumCapacitor()
        {
            var methodName = "CalculateMinimumCapacitor";
            LogTrace(methodName);

            var minimumCapacitorPercent = ((double)(_defensiveConfiguration.MinimumCapPct + 5)) / 100;
            var minimumCapacitor = _meCache.Ship.MaxCapacitor * minimumCapacitorPercent;

            var hardenerActivationCosts = _ship.ActiveHardenerModules.Sum(module => module.ActivationCost.GetValueOrDefault(0));
            minimumCapacitor += hardenerActivationCosts;

            var boosterActivationCosts = _ship.ShieldBoosterModules.Sum(module => module.ActivationCost.GetValueOrDefault(0));
            minimumCapacitor += boosterActivationCosts;

            LogMessage(methodName, LogSeverityTypes.Debug, "minimumCapPct: {0:F}, maxCap: {1}, hardenerCosts: {2:F}, boosterCosts: {3:F}, minimumCap: {4:F}",
                minimumCapacitorPercent, _meCache.Ship.MaxCapacitor, hardenerActivationCosts, boosterActivationCosts, minimumCapacitor);

            return minimumCapacitor;
        }

        /// <summary>
        /// Generate a value to offset delays between laser activation or deactivation.
        /// </summary>
        /// <param name="canBeNegative">True if the fudge factor can be negative, false if it cannot.</param>
        /// <returns>The value, in seconds, to offset laser activation. </returns>
        private int GenerateFudgeFactor(bool canBeNegative)
        {
            var fudgeFactorRandom = new Random();
            var fudgeFactor = fudgeFactorRandom.Next(0, 15);

            if (canBeNegative)
            {
                var positiveOrNegativeRandom = new Random();
                var positiveOrNegativeInt = positiveOrNegativeRandom.Next(0, 1);
                var isPositive = positiveOrNegativeInt == 1;

                if (!isPositive)
                    fudgeFactor *= -1;
            }

            return fudgeFactor;
        }

        /// <summary>
        /// Handle short-cycling of mining lasers. 
        /// </summary>
        /// <param name="miningLasers"></param>
        /// <returns></returns>
        private bool ShortCycleLasers(IEnumerable<EVE.ISXEVE.Interfaces.IModule> miningLasers)
        {
            var methodName = "ShortCycleLasers";
            LogTrace(methodName);

            if (_nextShortCycleDeactivation == null || DateTime.Now < _nextShortCycleDeactivation.Value) return false;

            var totalActivationCost = miningLasers.Select(module => module.ActivationCost.GetValueOrDefault(0)).Sum();
            var minimumCapacitor = CalculateMinimumCapacitor();

            //Only short cycle if I have capacitor for it
            if (_meCache.Ship.Capacitor - totalActivationCost < minimumCapacitor)
            {
                LogMessage(methodName, LogSeverityTypes.Debug, "Skipping short cycle because the total activation cost of {0} would put our current cap of {1} below minimum cap of {2}.",
                    totalActivationCost, _meCache.Ship.Capacitor, minimumCapacitor);
                return false;
            }

            //Un-set the short cycle time 
            _nextShortCycleDeactivation = null;

            //Welp, deactivate the lasers.
            _ship.DeactivateModuleList(miningLasers, true);
            return true;
        }

        /// <summary>
        /// Set the time of the next possible short cycle laser deactivation.
        /// </summary>
        /// <param name="miningLasers"></param>
        private void UpdateNextShortCycleDeactivation(IEnumerable<EVE.ISXEVE.Interfaces.IModule> miningLasers)
        {
            var methodName = "UpdateNextShortCycleDeactivation";
            LogTrace(methodName);

            var miningLaser = miningLasers.FirstOrDefault(laser => laser.ActivationTime != null);

            if (miningLaser == null)
            {
                LogMessage(methodName, LogSeverityTypes.Standard, "Error: No mining laser had a valid activation time.");
                return;
            }

            var activationTime = miningLaser.ActivationTime.GetValueOrDefault(0);

            //Use 1/3 cycle as the short cycle for ore harvesters and 
            double secondsUntilNextDelay;
            var typeId = miningLaser.ToItem.TypeID;
            if (typeId == (int)TypeIDs.Ice_Harvester_II || typeId == (int)TypeIDs.Ice_Harvester_I)
            {
                //Exhumers can short-cycle ice and still get a unit of ice, as their cycles yield two units.
                //Include a fudge factor to make sure we're halfway done with the cycle.
                var fudgeFactor = GenerateFudgeFactor(false);
                secondsUntilNextDelay = (activationTime / 2) + fudgeFactor;
            }
            else
            {
                var fudgeFactor = GenerateFudgeFactor(true);
                secondsUntilNextDelay = (activationTime / 3) + fudgeFactor;
            }

            _nextShortCycleDeactivation = DateTime.Now.AddSeconds(secondsUntilNextDelay);
        }

        /// <summary>
        /// Ensure the given mining laser is armed with a crystal appropriate for the given target, if such crystal is available.
        /// </summary>
        /// <param name="miningLaser"></param>
        /// <param name="target"></param>
        /// <returns>True if the loaded charge was modified, otherwise false.</returns>
        private bool EnsureMiningLaserIsArmedForTarget(EVE.ISXEVE.Interfaces.IModule miningLaser, IEntityWrapper target)
        {
            var methodName = "EnsureMiningLaserIsArmedForTarget";
            LogTrace(methodName, "Module: {0}, Target: {1}", miningLaser.ID, target.ID);

            if (miningLaser.ToItem.GroupID != (int)GroupIDs.FrequencyMiningLaser) return false;
            if (miningLaser.IsActive) return false;

            //Get the best possible mining crystal, including the crystal loaded
            var bestMiningCrystal = _ship.GetBestMiningCrystal(target, miningLaser);
            if (bestMiningCrystal == null) return false;

            if (!ShouldMiningLaserChangeToCrystal(miningLaser, bestMiningCrystal)) return false;

            //Get a matching reference from the module's available ammo
            var availableAmmo = miningLaser.GetAvailableAmmo();
            if (availableAmmo == null) return false;

            //If the charge isn't available, there's nothing more to do
            if (availableAmmo.All(item => item.ID != bestMiningCrystal.ID)) return false;

            LogMessage(methodName, LogSeverityTypes.Standard, "Changing the loaded crystal of module \"{0}\" ({1}) to \"{2}\".",
                miningLaser.ToItem.Name, miningLaser.ID, bestMiningCrystal.Name);
            miningLaser.ChangeAmmo(bestMiningCrystal.ID, 1);
            return true;
        }

        /// <summary>
        /// Determine if a given mining laser should use the given crystal.
        /// </summary>
        /// <param name="miningLaser"></param>
        /// <param name="bestMiningCrystal"></param>
        /// <returns>True if so, false otherwise.</returns>
        private static bool ShouldMiningLaserChangeToCrystal(EVE.ISXEVE.Interfaces.IModule miningLaser, Item bestMiningCrystal)
        {
            return LavishScriptObject.IsNullOrInvalid(miningLaser.Charge) || bestMiningCrystal.TypeID != miningLaser.Charge.TypeId;
        }

        /// <summary>
        /// Determine the lowest maximum range of our mining lasers.
        /// </summary>
        /// <returns></returns>
        private double GetLowestMaximumLaserRange()
        {
            // ReSharper disable PossibleInvalidOperationException
            var lowestMaximumLaserRange = _ship.MiningLaserModules.Select(laser => laser.OptimalRange)
                .Where(val => val.HasValue)
                .Select(val => val.Value)
                .Min();
            // ReSharper restore PossibleInvalidOperationException

            //Apply a margin of error
            //lowestMaximumLaserRange *= 0.95;
            return lowestMaximumLaserRange;
        }

        /// <summary>
        /// Make use of mining drones against the given prioritized mining targets.
        /// </summary>
        /// <param name="miningTargets">Mining targets, ordered by priority.</param>
        private void UseMiningDrones(IEnumerable<IEntityWrapper> miningTargets)
        {
            var methodName = "UseMiningDrones";
            LogTrace(methodName);

            if (!_miningConfiguration.UseMiningDrones) return;
            if (_miningConfiguration.IsIceMining) return;
            if (_drones.TotalDrones <= 0) return;

            //Firstly, determine the first target within a margin of error of drone range because drones should be focused on one asteroid.
            var effectiveDroneRange = _meCache.DroneControlDistance * 0.95;
            var firstTarget = miningTargets.FirstOrDefault(entity => entity.Distance < effectiveDroneRange);

            if (firstTarget == null)
            {
                LogMessage(methodName, LogSeverityTypes.Debug, "No mining targets were within the drone control range of {0}m.", effectiveDroneRange);
                return;
            }

            //Launch drones if necessary
            if (_drones.DronesInBay > 0 && _drones.CanLaunchDrones())
            {
                LogMessage(methodName, LogSeverityTypes.Standard, "Launching mining drones.");
                _drones.LaunchAllDrones();
                return;
            }

            //If there aren't drones in space, there's nothing more I can do.
            if (_drones.DronesInSpace == 0)
            {
                LogMessage(methodName, LogSeverityTypes.Debug, "No drones are in space.");
                return;
            }

            //If the drones are already on our target, we're done.
            if (!_drones.IsAnyDroneIdle && _drones.DroneTargetEntityId == firstTarget.ID) return;

            //Drones require an active target - ensure the chosen target is our active target.
            if (_meCache.ActiveTargetId != firstTarget.ID)
            {
                if (!_targeting.CanChangeTarget || !firstTarget.IsLockedTarget) return;

                //Make the chosen target the active target.
                LogMessage(methodName, LogSeverityTypes.Standard, "Making target \"{0}\" ({1}) active for mining drones.",
                    firstTarget.Name, firstTarget.ID);
                _targeting.ChangeTargetTo(firstTarget, true);
                return;
            }

            //Send the drones!
            _drones.SendAllDrones();
        }

        /// <summary>
        /// Obtain a list of mining targets, ordered by priority and sub-priority.
        /// Warning: These may not yet be locked targets.
        /// </summary>
        /// <returns></returns>
        private ICollection<IEntityWrapper> GetMiningTargets()
        {
            var methodName = "GetMiningTargets";
            LogTrace(methodName);

            var miningTargets = _targetQueue.Targets
                .Join(_entityProvider.EntityWrappers, queueTarget => queueTarget.Id, entity => entity.ID, (queueTarget, entity) => new { queueTarget, entity })
                .Where(pair => pair.queueTarget.Type == TargetTypes.Mine)
                .OrderBy(pair => pair.queueTarget.Priority)
                .ThenByDescending(pair => pair.queueTarget.SubPriority)
                .ThenBy(pair => pair.entity.Distance)
                .Select(pair => pair.entity)
                .ToList();

            return miningTargets;
        }

        private void TractorTarget(IQueueTarget queueTarget)
        {
            var methodName = "TractorTarget";
            LogTrace(methodName, "QueueTarget: {0}", queueTarget.Id);

            // Make sure this entity is requested for update
            _entityProvider.EntityWrappersById[queueTarget.Id].RequestObjectRefresh();

            var target = _entityProvider.EntityWrappersById[queueTarget.Id];

            // TractorTarget is now ONLY responsible for looting the active target when it's close
            // Module activation (tractors/salvagers) is handled by ManageIdleSalvageModules()

            if (target.Distance <= (int)Ranges.LootActivate)
            {
                // Loot if it needs looting (works for both wrecks and cargo containers)
                if (NeedsLooting(target))
                {
                    var cargoWindow = Core.Metatron.EveWindowProvider.GetWindowByItemId(target.ID);
                    if (LavishScriptObject.IsNullOrInvalid(cargoWindow))
                    {
                        var entityType = IsWreck(target) ? "wreck" : "container";
                        LogMessage(methodName, LogSeverityTypes.Standard, "Opening {0} \"{1}\" ({2})",
                                   entityType, target.Name, target.ID);
                        target.Open();
                    }
                    else
                    {
                        LogMessage(methodName, LogSeverityTypes.Standard, "Looting \"{0}\"", target.Name);
                        cargoWindow.LootAll();
                    }
                }
                else
                {
                    LogMessage(methodName, LogSeverityTypes.Debug, "\"{0}\" is empty, nothing to loot.", target.Name);
                }
            }

            // Switch to a better target if this one is done
            ChangeSalvageTarget(target);
        }

        private void ChangeSalvageTarget(IEntityWrapper target)
        {
            var methodName = "ChangeSalvageTarget";
            LogTrace(methodName, "Target: {0}", target.ID);


            //If I can't change target just return
            if (!_targeting.CanChangeTarget) return;

            var sortedTargets = GetSortedTractorTargets();

            //If there are no sortedTargets just return.
            if (sortedTargets.Count == 0) return;

            foreach (var sortedTarget in sortedTargets)
            {
                LogMessage(methodName, LogSeverityTypes.Debug, "Target: \'{0}\' ({1}, {2})",
                    sortedTarget.Name, sortedTarget.ID, _targeting.GetNumModulesOnEntity(sortedTarget.ID));
            }

            IEntityWrapper targetEntity;
            //Change target if necessary.
            if (_ship.TractorBeamModules.Any(m => m.Target.Distance < (int)Ranges.LootActivate) && _ship.TractorBeamModules.All(m => m.IsActive))
            {
                targetEntity = sortedTargets.OrderBy(t => t.Distance).First();
            }
            else
            {
                targetEntity = sortedTargets.First();
            }
            if (targetEntity.ID != target.ID)
            {
                LogMessage(methodName, LogSeverityTypes.Debug, "Making highest priority tractor target \'{0}\' ({1}) the active target.",
                    targetEntity.Name, targetEntity.ID);
                _targeting.ChangeTargetTo(targetEntity, false);
            }


        }

        private List<IEntityWrapper> GetSortedTractorTargets()
        {
            return GetSortedTargets(true, true, TargetTypes.LootSalvage);
        }

        private List<IEntityWrapper> GetSortedTargets(bool sortByNumModules, bool restrictByType, TargetTypes type)
        {
            var methodName = "GetSortedTargets";
            _logging.LogTrace(ModuleName, methodName, "Type: {0}, SortByNumModules: {1}", type, sortByNumModules);

            //Get a list of locked asteroids, sorted first by # of lasers and second by priority.
            List<IEntityWrapper> targets;
            if (restrictByType)
            {
                switch (type)
                {
                    case TargetTypes.Mine:
                        targets = _targeting.GetLockedMiningTargets();
                        break;
                    case TargetTypes.LootSalvage:
                        targets = _targeting.GetLockedTractorTargets();
                        break;
                    case TargetTypes.Kill:
                        targets = _targeting.GetLockedKillingTargets();
                        break;
                    default:
                        goto case TargetTypes.Mine;
                }
            }
            else
            {
                targets = Core.Metatron.MeCache.Targets.ToList();
            }

            if (sortByNumModules)
            {
                targets = (from IEntityWrapper ce in targets
                           join QueueTarget qt in _targetQueue.Targets on ce.ID equals qt.Id
                           orderby Core.Metatron.Targeting.GetNumModulesOnEntity(ce.ID) ascending, qt.Priority ascending,
                            qt.SubPriority descending, BoolToInt(_meCache.AttackersById.ContainsKey(ce.ID)) descending, qt.TimeQueued ascending
                           select ce).ToList();
            }
            else
            {
                targets = (from IEntityWrapper ce in targets
                           join QueueTarget qt in _targetQueue.Targets on ce.ID equals qt.Id
                           orderby qt.Priority ascending, qt.SubPriority descending, BoolToInt(_meCache.AttackersById.ContainsKey(ce.ID)) descending, qt.TimeQueued
                           select ce).ToList();
            }

            return targets;
        }

        public int TotalUnusedTractors
        {
            get
            {
                return _ship.TractorBeamModules.Count(module => !module.IsActive);
            }
        }
    }
    // ReSharper restore CompareOfFloatsByEqualityOperator
    // ReSharper restore PossibleMultipleEnumeration
    // ReSharper restore ConvertToConstant.Local
}

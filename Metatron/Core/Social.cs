using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using EVE.ISXEVE;
using LavishScriptAPI;
using Metatron.Core.Interfaces;

namespace Metatron.Core
{
	/* Social: Contain all social interaction, such as local checking, standings, etc */
    internal sealed class Social : ModuleBase, ISocial
    {
		//List of pilots for social iteration
		public bool IsLocalSafe { get; private set; }

	    private bool _isChatHandlerAttached;
		private readonly List<EVE_OnChannelMessageEventArgs> _localMessages = new List<EVE_OnChannelMessageEventArgs>();

        private readonly List<Pilot> _pilots = new List<Pilot>();
        public ReadOnlyCollection<Pilot> LocalPilots
        {
            get { return _pilots.AsReadOnly(); }
        }

        private readonly List<string> _hostilePilotNames = new List<string>();
        public ReadOnlyCollection<string> HostilePilotNamesInLocal
        {
            get { return _hostilePilotNames.AsReadOnly(); }
        }

        private readonly IIsxeveProvider _isxeveProvider;

		internal Social(IIsxeveProvider isxeveProvider)
		{
		    _isxeveProvider = isxeveProvider;

		    ModuleManager.ModulesToPulse.Add(this);
			ModuleName = "Social";
			PulseFrequency = 1;
		}

		public override bool Initialize()
		{
			if (!IsInitialized)
			{
				IsInitialized = true;
			}
			return IsInitialized;
		}

		public override bool OutOfFrameCleanup()
		{
			if (!IsCleanedUpOutOfFrame)
			{
				IsCleanedUpOutOfFrame = true;

                if (_isChatHandlerAttached)
                    LavishScript.Events.DetachEventTarget(EVE.ISXEVE.EVE.OnChannelMessageEvent, ChannelMessageReceived);
			}
			return IsCleanedUpOutOfFrame;
		}

		public override void InFrameCleanup()
		{
			foreach (var pilot in _pilots)
			{
				pilot.Invalidate();
			}
			_pilots.Clear();
		}

		public override void Pulse()
		{
			var methodName = "Pulse";
			LogTrace(methodName);

			if (!ShouldPulse()) 
				return;
			//StartPulseProfiling();

            if (Metatron.Config.SocialConfig.UseChatReading)
            {
                if (!_isChatHandlerAttached)
                {
                    _isChatHandlerAttached = true;
                    LavishScript.Events.AttachEventTarget(EVE.ISXEVE.EVE.OnChannelMessageEvent, ChannelMessageReceived);
                }
            }
            else
            {
                if (_isChatHandlerAttached)
                {
                    _isChatHandlerAttached = false;
                    LavishScript.Events.DetachEventTarget(EVE.ISXEVE.EVE.OnChannelMessageEvent, ChannelMessageReceived);
                }
            }

			//Lock local messages to block the event adding any
			lock (_localMessages)
			{
				//If we have any local messages, fire the alert
				if (_localMessages.Count > 0)
				{
					Metatron.Alerts.LocalChat(_localMessages[0].CharName, _localMessages[0].MessageText);
				}

				//Iterate any messages and log them then clear the list
				foreach (var channelMessageEventArgs in _localMessages)
				{
					LogMessage(methodName, LogSeverityTypes.Critical, "Channel {0} message: <{1}> {2}", 
						channelMessageEventArgs.ChannelID, channelMessageEventArgs.CharName, channelMessageEventArgs.MessageText);
				}
				_localMessages.Clear();
			}

			//StartMethodProfiling("GetLocalPilots");
		    _pilots.Clear();
            var pilots = _isxeveProvider.Eve.GetLocalPilots();
		    if (pilots != null)
		        _pilots.AddRange(pilots);
			//EndMethodProfiling();

			//StartMethodProfiling("IteratePilots");
			foreach (var pilot in _pilots)
			{
			    var charId = pilot.CharID;

			    if (charId == Metatron.MeCache.CharId) continue;
			    if (!Metatron.PilotCache.IsInitialized) continue;

			    if (Metatron.PilotCache.CachedPilotsById.ContainsKey(charId))
				{
					var tempCachedPilot = Metatron.PilotCache.CachedPilotsById[charId];
					//If I'm already getting the standing, might as well set it.
					tempCachedPilot.Standing = !Metatron.Config.DefenseConfig.DisableStandingsChecks
					                           	? new CachedStanding(pilot.Standing)
					                           	: new CachedStanding();

					var corp = pilot.Corp;
					var corpId = corp.ID;
					if (corpId >= 0 &&
					    (tempCachedPilot.Corp.Length == 0 ||
					     tempCachedPilot.CorpID != corpId))
					{
						tempCachedPilot.CorpID = corpId;
                        tempCachedPilot.Corp = GetCorporationName(corpId);
					}

					var allianceId = pilot.AllianceID;
					if (allianceId >= 0 &&
					    (tempCachedPilot.Alliance.Length == 0 ||
					     tempCachedPilot.AllianceID != allianceId))
					{
						//Core.Metatron.Logging.OnLogMessage(ObjectName, new LogEventArgs(LogSeverityTypes.Debug,
						//    "Pulse", String.Format("Setting pilot {0}'s Alliance to {1} ({2})",
						//    lp.Name, lp.Alliance, lp.AllianceID)));
						tempCachedPilot.AllianceID = allianceId;
					    tempCachedPilot.Alliance = GetAllianceName(allianceId);
					}

					if (allianceId < 0)
					{
						tempCachedPilot.AllianceID = allianceId;
						tempCachedPilot.Alliance = string.Empty;
					}
				}
				else
			    {
			        var corporationName = GetCorporationName(pilot.Corp.ID);
			        var allianceName = GetAllianceName(pilot.AllianceID);

                    Metatron.PilotCache.AddPilot(pilot, corporationName, allianceName);
				}
			}
			//EndMethodProfiling();

			//StartMethodProfiling("IsLocalSafe");
			IsLocalSafe = DetermineIfLocalIsSafe();
			//EndMethodProfiling();

			//EndPulseProfiling();
		}

        private string GetAllianceName(int allianceId)
        {
            if (allianceId <= 0) return string.Empty;

            if (!Metatron.AllianceCache.CachedAlliancesById.ContainsKey(allianceId))
            {
                Metatron.AllianceCache.GetAllianceInfo(allianceId);

                return string.Empty;
            }

            var alliance = Metatron.AllianceCache.CachedAlliancesById[allianceId];
            return alliance.Name;
        }

        private string GetCorporationName(Int64 corpId)
        {
            if (corpId <= 0) return string.Empty;

            if (!Metatron.CorporationCache.CachedCorporationsById.ContainsKey(corpId))
            {
                Metatron.CorporationCache.GetCorporationInfo(corpId);

                return string.Empty;
            }

            var corp = Metatron.CorporationCache.CachedCorporationsById[corpId];
            return corp.Name;
        }

        /// <summary>
		/// Process received local messages
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void ChannelMessageReceived(object sender, LSEventArgs e)
		{
			var methodName = "ChannelMessageReceived";
			LogTrace(methodName);

			var channelMessageEventArgs = new EVE_OnChannelMessageEventArgs(e);

			//Lock _localMessages for thread synch
			lock (_localMessages)
			{
				//Only watch for local messages. Here's how the IDs work.
				//Channel ID is the ID of the solarsystem for local, the ID of your corporation or alliance for those two channels,
				//or its own oddball number for other channels. So, check for the solar system ID.
				if (channelMessageEventArgs.ChannelID == Metatron.MeCache.SolarSystemId)
				{
					_localMessages.Add(channelMessageEventArgs);
				}
			}
		}

		//Determine if the system is safe.
		private bool DetermineIfLocalIsSafe()
		{
			var methodName = "DetermineIfLocalIsSafe";
			LogTrace(methodName);

			// Clear hostile list at start of each check
			_hostilePilotNames.Clear();

			//If I'm the only person in system
            if (Metatron.MeCache.SolarSystemId < 0 || _isxeveProvider.Eve.GetLocalPilots().Count == 1)
				return true;

			foreach (var pilot in _pilots)
			{
				var charId = pilot.CharID;

                if (charId == Metatron.MeCache.CharId)
                    continue;

				if (!Metatron.PilotCache.CachedPilotsById.ContainsKey(charId))
				{
					LogMessage(methodName, LogSeverityTypes.Standard, "Error: could not find entry in PilotCache for pilot {0} ({1}).",
                               pilot.Name, charId);
					_hostilePilotNames.Add(pilot.Name);
					return false;
				}

				var tempCachedPilot = Metatron.PilotCache.CachedPilotsById[charId];

				if (Metatron.Config.DefenseConfig.RunOnNonWhitelistedPilot)
				{
					//Note: If they're in ANY whitelist, they're ok unless excluded by a blacklist.

					if ((tempCachedPilot.AllianceID < 0 || !Metatron.Config.SocialConfig.AllianceWhitelist.Contains(tempCachedPilot.Alliance)) &&
						!Metatron.Config.SocialConfig.CorpWhitelist.Contains(tempCachedPilot.Corp) &&
						!Metatron.Config.SocialConfig.PilotWhitelist.Contains(tempCachedPilot.Name))
					{
						LogMessage(methodName, LogSeverityTypes.Critical, "Player not whitelisted: {0}, ID: {1}, Corp: {2}, Alliance: {3}",
								   tempCachedPilot.Name, tempCachedPilot.CharID, tempCachedPilot.Corp, tempCachedPilot.Alliance);
						Metatron.Alerts.LocalUnsafe(tempCachedPilot.Name, tempCachedPilot.Corp, tempCachedPilot.Alliance);
						_hostilePilotNames.Add(tempCachedPilot.Name);
						return false;
					}
				}
				if (Metatron.Config.DefenseConfig.RunOnBlacklistedPilot)
				{
					//If we find 'em in any blacklist, we're not safe.
					var isPlayerAllianceBlacklisted = Metatron.Config.SocialConfig.AllianceBlacklist.Contains(tempCachedPilot.Alliance);
					if (isPlayerAllianceBlacklisted)
					{
                        LogMessage(methodName, LogSeverityTypes.Debug, "LocalPilot {0}, MTP: {1}, MTC: {2}, CTP: {3}, CTC: {4}, CTA: {5}, ATA: {6}",
                            tempCachedPilot.Name, tempCachedPilot.Standing.MeToPilot, tempCachedPilot.Standing.MeToCorp, tempCachedPilot.Standing.CorpToPilot,
                            tempCachedPilot.Standing.CorpToCorp, tempCachedPilot.Standing.CorpToAlliance, tempCachedPilot.Standing.AllianceToAlliance);
						LogMessage(methodName, LogSeverityTypes.Critical, "Player is alliance blacklisted: {0}, ID: {1}, Corp: \"{2}\", Alliance: \"{3}\"",
						           tempCachedPilot.Name, tempCachedPilot.CharID, tempCachedPilot.Corp, tempCachedPilot.Alliance);
						Metatron.Alerts.LocalUnsafe(tempCachedPilot.Name, tempCachedPilot.Corp, tempCachedPilot.Alliance);
						_hostilePilotNames.Add(tempCachedPilot.Name);
						return false;
					}

					var isPlayerCorpBlacklisted = Metatron.Config.SocialConfig.CorpBlacklist.Contains(tempCachedPilot.Corp);
					if (isPlayerCorpBlacklisted)
					{
						LogMessage(methodName, LogSeverityTypes.Critical, "Player is corporation blacklisted: {0}, ID: {1}, Corp: \"{2}\", Alliance: \"{3}\"",
						           tempCachedPilot.Name, tempCachedPilot.CharID, tempCachedPilot.Corp, tempCachedPilot.Alliance);
						Metatron.Alerts.LocalUnsafe(tempCachedPilot.Name, tempCachedPilot.Corp, tempCachedPilot.Alliance);
						_hostilePilotNames.Add(tempCachedPilot.Name);
						return false;
					}

					var isPlayerPiotBlacklisted = Metatron.Config.SocialConfig.PilotBlacklist.Contains(tempCachedPilot.Name);
					if (isPlayerPiotBlacklisted)
					{
						LogMessage(methodName, LogSeverityTypes.Critical, "Player is pilot blacklisted: {0}, ID: {1}, Corp: \"{2}\", Alliance: \"{3}\"",
						           tempCachedPilot.Name, tempCachedPilot.CharID, tempCachedPilot.Corp, tempCachedPilot.Alliance);
						Metatron.Alerts.LocalUnsafe(tempCachedPilot.Name, tempCachedPilot.Corp, tempCachedPilot.Alliance);
						_hostilePilotNames.Add(tempCachedPilot.Name);
						return false;
					}
				}

				//If not checking standings, return/continue
				if (Metatron.Config.DefenseConfig.DisableStandingsChecks)
					continue;

				//Temporary Standing to check
				var cachedStanding = tempCachedPilot.Standing;
				//I've got two ways of doing this:
				//1) Check if all *ToPilot is lower than minimum pilot standing.
				//2) Check if ANY *ToPilot is lower than min pilot standing.
				//#1 could caues issues when: dunno, can't think of any
				//#2 could cause issues when someone is blue to corp but not to you, thereby being "danger"

				var standingsCheckPassed = CheckStandings(cachedStanding, tempCachedPilot);
				LogTrace(methodName, "StandingsCheckPassed:", standingsCheckPassed, tempCachedPilot.CharID);
				if (standingsCheckPassed) continue;
				else return standingsCheckPassed;

     //           if ((Metatron.Config.DefenseConfig.RunOnCorpToPilot && cachedStanding.CorpToPilot < Metatron.Config.SocialConfig.MinimumPilotStanding &&
     //                Metatron.Config.DefenseConfig.RunOnMeToPilot && cachedStanding.MeToPilot < Metatron.Config.SocialConfig.MinimumPilotStanding) ||
     //               (Metatron.Config.DefenseConfig.RunOnMeToPilot && cachedStanding.MeToPilot < Metatron.Config.SocialConfig.MinimumPilotStanding))
     //           {
     //               LogMessage(methodName, LogSeverityTypes.Critical, "Pilot {0}'s CorpToPilot ({1}) or MeToPilot ({2}) standing is below minimum pilot standing of {3}!",
     //                   tempCachedPilot.Name, cachedStanding.CorpToPilot, cachedStanding.MeToPilot, Metatron.Config.SocialConfig.MinimumPilotStanding);
     //               Metatron.Alerts.LocalUnsafe(tempCachedPilot.Name, tempCachedPilot.Corp, tempCachedPilot.Alliance);
     //               return false;
     //           }

     //           //Check the *ToCorp now
     //           if (tempCachedPilot.CorpID != Core.Metatron.MeCache.CorporationId &&
					//((Metatron.Config.DefenseConfig.RunOnCorpToCorp && cachedStanding.CorpToCorp < Metatron.Config.SocialConfig.MinimumCorpStanding &&
     //                Metatron.Config.DefenseConfig.RunOnMeToCorp && cachedStanding.MeToCorp < Metatron.Config.SocialConfig.MinimumCorpStanding) ||
     //               (Metatron.Config.DefenseConfig.RunOnMeToCorp && cachedStanding.MeToCorp < Metatron.Config.SocialConfig.MinimumCorpStanding)))
     //           {
     //               LogMessage(methodName, LogSeverityTypes.Critical, "Pilot {0}'s CorpToCorp ({1}) or MeToCorp ({2}) standing is below minimum corp standing of {3}!",
     //                   tempCachedPilot.Name, cachedStanding.CorpToCorp, cachedStanding.MeToCorp, Metatron.Config.SocialConfig.MinimumCorpStanding);
     //               Metatron.Alerts.LocalUnsafe(tempCachedPilot.Name, tempCachedPilot.Corp, tempCachedPilot.Alliance);
     //               return false;
     //           }
     //           //And now check *ToAlliance
     //           if (tempCachedPilot.AllianceID != Core.Metatron.MeCache.AllianceId && 
					//((Metatron.Config.DefenseConfig.RunOnAllianceToAlliance && cachedStanding.AllianceToAlliance < Metatron.Config.SocialConfig.MinimumAllianceStanding &&
     //               Metatron.Config.DefenseConfig.RunOnCorpToAlliance && cachedStanding.CorpToAlliance < Metatron.Config.SocialConfig.MinimumAllianceStanding) ||
     //               (Metatron.Config.DefenseConfig.RunOnCorpToAlliance && cachedStanding.CorpToAlliance < Metatron.Config.SocialConfig.MinimumAllianceStanding)))
     //           {
     //               LogMessage(methodName, LogSeverityTypes.Critical, "Pilot {0}'s CorpToAlliance ({1}) or AllianceToAlliance ({2}) standing is below minimum alliance standing of {3}!",
     //                   tempCachedPilot.Name, cachedStanding.CorpToAlliance, cachedStanding.AllianceToAlliance, Metatron.Config.SocialConfig.MinimumAllianceStanding);
     //               Metatron.Alerts.LocalUnsafe(tempCachedPilot.Name, tempCachedPilot.Corp, tempCachedPilot.Alliance);
     //               return false;
     //           }
            }
			return true;
		}

        private bool CheckStandings(CachedStanding cachedStanding, CachedPilot cachedPilot)
        {
			var methodName = "CheckStandings";
			LogTrace(methodName);

			// Don't check standings for corpmates or alliance members
			if (cachedPilot.CorpID == Core.Metatron.MeCache.CorporationId ||
				(cachedPilot.AllianceID == Core.Metatron.MeCache.AllianceId && cachedPilot.AllianceID > 0))
			{
				return true;
			}

			// Check individual standing buttons (existing 6 buttons)
			bool individualChecksPassed = CheckIndividualStandings(cachedStanding, cachedPilot);
			if (!individualChecksPassed)
			{
				return false; // Individual checks failed - FLEE
			}

			// Check new Corps/Alliances button
			bool corpsAlliancesCheckPassed = CheckCorpsAlliancesStanding(cachedStanding, cachedPilot);
			if (!corpsAlliancesCheckPassed)
			{
				return false; // Corps/Alliances check failed - FLEE
			}

			return true; // All enabled checks passed - SAFE
        }

		private bool CheckIndividualStandings(CachedStanding cachedStanding, CachedPilot cachedPilot)
		{
			var methodName = "CheckIndividualStandings";

			// FIXED: OR logic - if ANY enabled check fails, flee immediately

			if (Core.Metatron.Config.DefenseConfig.RunOnMeToPilot &&
				cachedStanding.MeToPilot < Core.Metatron.Config.SocialConfig.MinimumPilotStanding)
			{
				LogMessage(methodName, LogSeverityTypes.Critical,
					$"Pilot {cachedPilot.Name} MeToPilot standing ({cachedStanding.MeToPilot}) is below minimum of {Core.Metatron.Config.SocialConfig.MinimumPilotStanding}");

				// Check negative failsafe
				if (Core.Metatron.Config.DefenseConfig.RunOnNegativeFailsafe && cachedStanding.MeToPilot < 0)
				{
					LogMessage(methodName, LogSeverityTypes.Critical,
						$"NEGATIVE FAILSAFE: MeToPilot is negative ({cachedStanding.MeToPilot})!");
				}

				_hostilePilotNames.Add(cachedPilot.Name);
				return false; // FLEE IMMEDIATELY
			}

			if (Core.Metatron.Config.DefenseConfig.RunOnMeToCorp &&
				cachedStanding.MeToCorp < Core.Metatron.Config.SocialConfig.MinimumCorpStanding)
			{
				LogMessage(methodName, LogSeverityTypes.Critical,
					$"Pilot {cachedPilot.Name} MeToCorp standing ({cachedStanding.MeToCorp}) is below minimum of {Core.Metatron.Config.SocialConfig.MinimumCorpStanding}");

				if (Core.Metatron.Config.DefenseConfig.RunOnNegativeFailsafe && cachedStanding.MeToCorp < 0)
				{
					LogMessage(methodName, LogSeverityTypes.Critical,
						$"NEGATIVE FAILSAFE: MeToCorp is negative ({cachedStanding.MeToCorp})!");
				}

				_hostilePilotNames.Add(cachedPilot.Name);
				return false; // FLEE IMMEDIATELY
			}

			if (Core.Metatron.Config.DefenseConfig.RunOnCorpToPilot &&
				cachedStanding.CorpToPilot < Core.Metatron.Config.SocialConfig.MinimumPilotStanding)
			{
				LogMessage(methodName, LogSeverityTypes.Critical,
					$"Pilot {cachedPilot.Name} CorpToPilot standing ({cachedStanding.CorpToPilot}) is below minimum of {Core.Metatron.Config.SocialConfig.MinimumPilotStanding}");

				if (Core.Metatron.Config.DefenseConfig.RunOnNegativeFailsafe && cachedStanding.CorpToPilot < 0)
				{
					LogMessage(methodName, LogSeverityTypes.Critical,
						$"NEGATIVE FAILSAFE: CorpToPilot is negative ({cachedStanding.CorpToPilot})!");
				}

				_hostilePilotNames.Add(cachedPilot.Name);
				return false; // FLEE IMMEDIATELY
			}

			if (Core.Metatron.Config.DefenseConfig.RunOnCorpToCorp &&
				cachedStanding.CorpToCorp < Core.Metatron.Config.SocialConfig.MinimumCorpStanding)
			{
				LogMessage(methodName, LogSeverityTypes.Critical,
					$"Pilot {cachedPilot.Name} CorpToCorp standing ({cachedStanding.CorpToCorp}) is below minimum of {Core.Metatron.Config.SocialConfig.MinimumCorpStanding}");

				if (Core.Metatron.Config.DefenseConfig.RunOnNegativeFailsafe && cachedStanding.CorpToCorp < 0)
				{
					LogMessage(methodName, LogSeverityTypes.Critical,
						$"NEGATIVE FAILSAFE: CorpToCorp is negative ({cachedStanding.CorpToCorp})!");
				}

				_hostilePilotNames.Add(cachedPilot.Name);
				return false; // FLEE IMMEDIATELY
			}

			if (Core.Metatron.Config.DefenseConfig.RunOnCorpToAlliance &&
				cachedStanding.CorpToAlliance < Core.Metatron.Config.SocialConfig.MinimumAllianceStanding)
			{
				LogMessage(methodName, LogSeverityTypes.Critical,
					$"Pilot {cachedPilot.Name} CorpToAlliance standing ({cachedStanding.CorpToAlliance}) is below minimum of {Core.Metatron.Config.SocialConfig.MinimumAllianceStanding}");

				if (Core.Metatron.Config.DefenseConfig.RunOnNegativeFailsafe && cachedStanding.CorpToAlliance < 0)
				{
					LogMessage(methodName, LogSeverityTypes.Critical,
						$"NEGATIVE FAILSAFE: CorpToAlliance is negative ({cachedStanding.CorpToAlliance})!");
				}

				_hostilePilotNames.Add(cachedPilot.Name);
				return false; // FLEE IMMEDIATELY
			}

			if (Core.Metatron.Config.DefenseConfig.RunOnAllianceToAlliance &&
				cachedStanding.AllianceToAlliance < Core.Metatron.Config.SocialConfig.MinimumAllianceStanding)
			{
				LogMessage(methodName, LogSeverityTypes.Critical,
					$"Pilot {cachedPilot.Name} AllianceToAlliance standing ({cachedStanding.AllianceToAlliance}) is below minimum of {Core.Metatron.Config.SocialConfig.MinimumAllianceStanding}");

				if (Core.Metatron.Config.DefenseConfig.RunOnNegativeFailsafe && cachedStanding.AllianceToAlliance < 0)
				{
					LogMessage(methodName, LogSeverityTypes.Critical,
						$"NEGATIVE FAILSAFE: AllianceToAlliance is negative ({cachedStanding.AllianceToAlliance})!");
				}

				_hostilePilotNames.Add(cachedPilot.Name);
				return false; // FLEE IMMEDIATELY
			}

			return true; // All enabled checks passed - SAFE
		}

		private bool CheckCorpsAlliancesStanding(CachedStanding cachedStanding, CachedPilot cachedPilot)
		{
			var methodName = "CheckCorpsAlliancesStanding";

			// If the combined check isn't enabled, just return true (safe)
			if (!Core.Metatron.Config.DefenseConfig.RunOnCorpsAlliances)
			{
				return true;
			}

			int minStanding = Core.Metatron.Config.SocialConfig.MinimumCorpsAlliancesStanding;

			// Check for negative failsafe FIRST (if enabled)
			if (Core.Metatron.Config.DefenseConfig.RunOnNegativeFailsafe)
			{
				if (cachedStanding.MeToCorp < 0)
				{
					LogMessage(methodName, LogSeverityTypes.Critical,
						$"NEGATIVE FAILSAFE: Pilot {cachedPilot.Name} MeToCorp is negative ({cachedStanding.MeToCorp})!");
					_hostilePilotNames.Add(cachedPilot.Name);
					return false; // FLEE
				}
				if (cachedStanding.CorpToCorp < 0)
				{
					LogMessage(methodName, LogSeverityTypes.Critical,
						$"NEGATIVE FAILSAFE: Pilot {cachedPilot.Name} CorpToCorp is negative ({cachedStanding.CorpToCorp})!");
					_hostilePilotNames.Add(cachedPilot.Name);
					return false;
				}
				if (cachedStanding.CorpToAlliance < 0)
				{
					LogMessage(methodName, LogSeverityTypes.Critical,
						$"NEGATIVE FAILSAFE: Pilot {cachedPilot.Name} CorpToAlliance is negative ({cachedStanding.CorpToAlliance})!");
					_hostilePilotNames.Add(cachedPilot.Name);
					return false;
				}
				if (cachedStanding.AllianceToAlliance < 0)
				{
					LogMessage(methodName, LogSeverityTypes.Critical,
						$"NEGATIVE FAILSAFE: Pilot {cachedPilot.Name} AllianceToAlliance is negative ({cachedStanding.AllianceToAlliance})!");
					_hostilePilotNames.Add(cachedPilot.Name);
					return false;
				}
			}

			// NEW LOGIC: If ANY of these 4 checks is > minimum, DON'T flee
			// If ALL 4 checks are <= minimum, FLEE

			bool meToCorpPasses = cachedStanding.MeToCorp > minStanding;
			bool corpToCorpPasses = cachedStanding.CorpToCorp > minStanding;
			bool corpToAlliancePasses = cachedStanding.CorpToAlliance > minStanding;
			bool allianceToAlliancePasses = cachedStanding.AllianceToAlliance > minStanding;

			// If ANY check passes (> minimum), safe (don't flee)
			if (meToCorpPasses || corpToCorpPasses || corpToAlliancePasses || allianceToAlliancePasses)
			{
				LogMessage(methodName, LogSeverityTypes.Debug,
					$"Pilot {cachedPilot.Name} passed Corps/Alliances check (at least one standing > {minStanding})");
				return true; // SAFE
			}

			// All checks failed (all <= minimum) - FLEE
			LogMessage(methodName, LogSeverityTypes.Critical,
				$"Pilot {cachedPilot.Name} FAILED Corps/Alliances check (ALL standings <= {minStanding}): " +
				$"MeToCorp={cachedStanding.MeToCorp}, CorpToCorp={cachedStanding.CorpToCorp}, " +
				$"CorpToAlliance={cachedStanding.CorpToAlliance}, AllianceToAlliance={cachedStanding.AllianceToAlliance}");
			_hostilePilotNames.Add(cachedPilot.Name);
			return false; // FLEE
		}
    }
}

﻿using Common;
using LogParserConcept.Models;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LogParserConcept
{
    public static class Methods
    {
        const string ParentPath = @"F:\PrancingTurtle\Output";
        /// <summary>
        /// The files to be generated within each encounter
        /// </summary>
        static Dictionary<EncounterContainerType, string> encounterContainers = new Dictionary<EncounterContainerType, string>
        {
            { EncounterContainerType.Damage, "damage.txt"},
            { EncounterContainerType.Heal, "heal.txt"},
            { EncounterContainerType.Shield, "shield.txt"},
            { EncounterContainerType.Buff, "buff.txt"},
            { EncounterContainerType.Debuff, "debuff.txt"},
            { EncounterContainerType.Death, "death.txt"}
        };

        private static Regex _isInt = new Regex("^[0-9]+$");

        /// <summary>
        /// The following list is used for specific fights that require a custom downtime length in order to avoid splitting parses
        /// </summary>
        private static List<DowntimeOverride> DowntimeOverrides = new List<DowntimeOverride>()
        {
            new DowntimeOverride{NpcName = "Prince Hylas", DowntimeSeconds = 50},
            new DowntimeOverride{NpcName = "Aqualix", DowntimeSeconds = 30},
            new DowntimeOverride{NpcName = "Denizar", DowntimeSeconds = 30}
        };

        private const int DefaultEncounterDowntime = 15; // 15 seconds

        public static async Task ParseAsync(SessionLogInfo logInfo, string logFile)
        {
            // Check the filesystem first
            var sessionPath = Path.Combine(ParentPath, logInfo.SessionId.ToString());
            if (CheckFolderExists(sessionPath) == false)
            {
                Console.WriteLine("Can't continue as something went wrong while checking that the session folder exists.");
                Console.ReadLine();
                Environment.Exit(1);
            }

            Console.WriteLine("Folder structure checked OK.");

            Console.WriteLine($"Beginning to parse {logFile}");
            await using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            using var sr = new StreamReader(fs);
            // TODO: Stop being lazy while testing and check for the actual timezone.
            // TODO: Stop being lazy while testing and check for the log type instead of assuming that we're logging with Standard.
            var logType = LogType.Standard;

            // Main loop
            var line = "";
            var lineNumber = 1;
            var downtimeSeconds = DefaultEncounterDowntime;
            int encounterNumber = 0;
            bool inCombat = false;
            var currentCombatStarted = new DateTime();
            var currentCombatLastDamage = new DateTime();
            double daysToAdd = 0;
            var lastTimeStamp = new DateTime();
            //var calculatedTimestamp = new DateTime();
            var encounterLength = new TimeSpan(0,0,0,0);

            var players = new HashSet<string>();
            var npcs = new HashSet<string>();
            var pets = new HashSet<string>();
            var abilities = new HashSet<string>();

            // Testing
            Stopwatch encWriter = new Stopwatch();

            while ((line = sr.ReadLine()) != null)
            {
                var entry = await ParseLine(line);
                if (entry.ValidEntry == false)
                {
                    if (!line.Contains("Combat Begin") && !line.Contains("Combat End"))
                    {
                        Console.WriteLine($"Line {lineNumber} is not valid ({entry.InvalidReason})");
                        Console.WriteLine(line);
                        Console.WriteLine();
                    }
                    // Skip the rest of this loop
                    continue;
                }

                if (!inCombat)
                {
                    // Not in combat yet. Check to see whether this particular log entry should 'start' combat
                    if (entry.ShouldStartCombat())
                    {
                        // Blank line just to be nice
                        Console.WriteLine();

                        // Begin combat.
                        encounterNumber++;
                        encounterLength = new TimeSpan(0,0,0,0);
                        Console.WriteLine($"Encounter {encounterNumber} started at {entry.ParsedTimeStamp.AddDays(daysToAdd)}");
                        // Default the encounter time unless it needs to be overridden
                        downtimeSeconds = entry.GetDowntimeValueForEncounter();
                        if (downtimeSeconds != DefaultEncounterDowntime)
                        {
                            Console.WriteLine($"Detected an overridden encounter downtime. The value is now {downtimeSeconds}");
                        }
                        inCombat = true;

                        // Set the variables that we'll use to determine when the encounter should end
                        currentCombatStarted = entry.ParsedTimeStamp.AddDays(daysToAdd);
                        lastTimeStamp = entry.ParsedTimeStamp.AddDays(daysToAdd);
                        currentCombatLastDamage = entry.ParsedTimeStamp.AddDays(daysToAdd);

                        // Update the time elapsed for this event
                        entry.SetTimeElapsed(currentCombatStarted);

                        // Create the files that we'll use for this encounter
                        var encounterContainersCreated = CreateEncounterContainers(sessionPath, encounterNumber);
                        if (!encounterContainersCreated)
                        {
                            Console.WriteLine($"Unable to create containers for encounter {encounterNumber}.");
                        }

                        #region Single line append
                        encWriter.Reset();
                        encWriter.Start();
                        // NB: This is only used to append lines one at a time.
                        // Add this entry to the file that it belongs to
                        await AppendLine(sessionPath, encounterNumber, encounterContainers[entry.ContainerType], line);
                        //Console.WriteLine($"{entry.SecondsElapsed}: {line}");
                        #endregion
                    }
                }
                else
                {
                    var secondDifference =
                        (int)(entry.ParsedTimeStamp.AddDays(daysToAdd) - lastTimeStamp).TotalSeconds;
                    if (secondDifference == 0 || secondDifference > 0)
                    {
                        // Timestamp hasn't changed, or it's later in the same day
                        entry.CalculatedTimeStamp = entry.ParsedTimeStamp.AddDays(daysToAdd);
                    }
                    else
                    {
                        // We have just rolled over midnight 
                        daysToAdd++;
                        entry.CalculatedTimeStamp = entry.ParsedTimeStamp.AddDays(daysToAdd);
                        //currentCombatLastDamage = calculatedTimestamp;
                    }

                    // Update the time elapsed for this event
                    entry.SetTimeElapsed(currentCombatStarted);

                    if ((entry.CalculatedTimeStamp - currentCombatLastDamage).TotalSeconds > downtimeSeconds)
                    {
                        encWriter.Stop();
                        inCombat = false;
                        encounterLength = currentCombatLastDamage - currentCombatStarted;
                        Console.WriteLine($"Combat for encounter {encounterNumber} ended at {entry.ParsedTimeStamp.AddDays(daysToAdd)} ({entry.SecondsElapsed} seconds elapsed). Time elapsed for reading and writing: {encWriter.Elapsed}");
                        Console.WriteLine($"The last damage was detected at {currentCombatLastDamage}");
                        var encInfo = new List<string>
                        {
                            $"Encounter {encounterNumber}",
                            $"Started: {currentCombatStarted}",
                            $"Ended: {currentCombatLastDamage}",
                            $"Duration: {encounterLength}"
                        };
                        await WriteEncounterInfo(sessionPath, encounterNumber, encInfo);

                        // Remove the encounter folder if it's not long enough to warrant saving
                        encounterLength = currentCombatLastDamage - currentCombatStarted;
                        if (encounterLength.TotalSeconds < 5)
                        {
                            // Remove the encounter (not long enough)
                            var encRemoved = RemoveEncounterFolder(sessionPath, encounterNumber);
                            if (encRemoved)
                            {
                                Console.WriteLine($"Encounter {encounterNumber} removed (<5s)");
                            }
                        }
                    }
                    // Still in combat but not outside our downtime period. Write the event if we need to
                    else if (!entry.IgnoreThisEvent)
                    {
                        // Update the last combat timestamp if it has changed
                        if (entry.CalculatedTimeStamp != currentCombatLastDamage)
                        {
                            if (entry.TargetType == CharacterType.Npc && entry.IsDamageType)
                            {
                                currentCombatLastDamage = entry.ParsedTimeStamp.AddDays(daysToAdd);
                            }
                        }

                        switch (entry.ContainerType)
                        {
                            case EncounterContainerType.Unknown:
                                Console.WriteLine($"  Unknown container type. Line: {line}");
                                break;
                            case EncounterContainerType.NotLogged:
                                break;
                            default:
                                //Console.WriteLine($"{entry.SecondsElapsed}: {line}");
                                await AppendLine(sessionPath, encounterNumber, encounterContainers[entry.ContainerType], line);
                                switch (entry.AttackerType)
                                {
                                    case CharacterType.Player:
                                        players.Add(entry.AttackerName);
                                        break;
                                    case CharacterType.Npc:
                                        npcs.Add(entry.AttackerName);
                                        break;
                                    case CharacterType.Pet:
                                        pets.Add(entry.AttackerName);
                                        break;
                                }
                                switch (entry.TargetType)
                                {
                                    case CharacterType.Player:
                                        players.Add(entry.TargetName);
                                        break;
                                    case CharacterType.Npc:
                                        npcs.Add(entry.TargetName);
                                        break;
                                    case CharacterType.Pet:
                                        pets.Add(entry.TargetName);
                                        break;
                                }

                                abilities.Add(entry.AbilityName);
                                break;
                        }
                    }
                }

                lineNumber++;
            }
        }

        public static async Task<LogEntry> ParseLine(string logLine)
        {
            var entry = new LogEntry();

            #region Validity
            // Check whether this line is valid first
            if (logLine.IndexOf('(') == -1 || logLine.IndexOf(')') == -1 || logLine.Length <= 22)
            {
                // Line length is invalid, or no usable data was found
                entry.InvalidReason = "Line length is invalid, or no usable data was found";
                return entry;
            }

            // Bracket count
            if (CountOccurrences(logLine, '(') != CountOccurrences(logLine, ')'))
            {
                entry.InvalidReason = "Bracket counts are not equal";
                return entry;
            }

            // Date parsing
            if (!DateTime.TryParse(logLine.Substring(0, 8), out var entryDate))
            {
                entry.InvalidReason = "Date couldn't be parsed";
                return entry;
            }

            entry.ParsedTimeStamp = entryDate;

            // Ensure the log line doesn't contain data from two events, like this:
            // 22:10:29: ( 7 , T=P#R=C#169025725536183234 , T=P#R=C#169025725536183234 , T=X#R=X#0 , T=X#R=X#0 , Geryonn , Geryonn , 0 , 1660365089 , Virulent Poison , 0 ) Geryon22:11:14: ( 27 , T=P#R=R#169025725533810537 , T=P#R=R#169025725533810537 , T=X#R=X#0 , T=X#R=X#0 , Killings , Killings , 25 , 939734518 , Archaic Tablet , 0 ) Killings's Archaic Tablet gives Killings 25 Mana.
            // If it does, skip them both. Unfortunate, but shit happens.

            // Hash count - should be 8.
            if (CountOccurrences(logLine, '#') != 8)
            {
                entry.InvalidReason = "Hash count is not 8";
                return entry;
            }

            #region Determine what part of this line is the 'log data'
            // Count how many open and close brackets we have on this line
            var openBrackets = new List<int>();
            var closeBrackets = new List<int>();

            var lineNoTimestamp = logLine.Substring(9).Trim();

            for (var i = lineNoTimestamp.IndexOf('('); i > -1; i = lineNoTimestamp.IndexOf('(', i + 1))
            {
                openBrackets.Add(i);
            }
            for (var i = lineNoTimestamp.IndexOf(')'); i > -1; i = lineNoTimestamp.IndexOf(')', i + 1))
            {
                closeBrackets.Add(i);
            }

            if (openBrackets.Count == 0)
            {
                return null;
            }

            int logDataStartIndex = openBrackets[0];
            int openBeforeFirstClose = openBrackets.Count(t => t < closeBrackets[0]);
            int logDataEndIndex = closeBrackets[openBeforeFirstClose - 1];

            var logData = lineNoTimestamp.Substring(logDataStartIndex + 1, logDataEndIndex - logDataStartIndex - 1).Trim().ReplaceInvalidAbilityNames();
            var logDetail = logData.Split(',');

            // There should be 11 elements in the log detail array
            if (logDetail.Length != 11)
            {
                entry.InvalidReason = "Log detail length is invalid ( != 11 )";
                return entry;
            }
            #endregion

            entry.ValidEntry = true;
            #endregion
            #region Data

            #region Process the 'data' part of the line first
            int actionTypeId = int.Parse(logDetail[0].Trim());
            entry.ActionType = _isInt.IsMatch(((ActionType)actionTypeId).ToString()) ? ActionType.Unknown : ((ActionType)actionTypeId);

            #region Attacking Character
            entry.AttackerType = GetCharacterType(logDetail[1].Trim(), out var attId);
            entry.AttackerId = attId;
            #endregion
            #region Target Character
            entry.TargetType = GetCharacterType(logDetail[2].Trim(), out var tarId);
            entry.TargetId = tarId;
            #endregion

            GetCharacterType(logDetail[3].Trim(), out var attPetOwnerId);
            GetCharacterType(logDetail[4].Trim(), out var tarPetOwnerId);
            if (attPetOwnerId != "0")
            {
                entry.AttackerPetOwnerId = attPetOwnerId;
            }
            if (tarPetOwnerId != "0")
            {
                entry.TargetPetOwnerId = tarPetOwnerId;
            }
            entry.AttackerName = logDetail[5].Trim();
            entry.TargetName = logDetail[6].Trim();
            entry.ActionValue = long.Parse(logDetail[7].Trim());
            entry.AbilityId = long.Parse(logDetail[8].Trim());
            entry.AbilityName = logDetail[9].Trim();
            entry.SpecialValue = long.Parse(logDetail[10].Trim());

            #endregion
            #region Process the 'message' part of the line, that incidentally has the damage type as well as additional info
            entry.Message = lineNoTimestamp.Remove(logDataStartIndex, logDataEndIndex - logDataStartIndex + 1).Trim();
            // Check if the message contains special event info, like absorbs, intercepts, etc
            entry.ProcessSpecial();
            // Get damage type if we can (damaging abilities only)
            if (!string.IsNullOrEmpty(entry.TargetName) &&
                (entry.ActionType == ActionType.DamageCrit ||
                 entry.ActionType == ActionType.NormalDamageNonCrit ||
                 entry.ActionType == ActionType.DotDamageNonCrit ||
                 entry.ActionType == ActionType.Block))
            {
                entry.ProcessAbilityDamageType();
            }
            #endregion
            // Determine if we want to ignore this line
            if (entry.IsDeathType && entry.OverKillAmount == entry.TotalDamage)
            {
                entry.IgnoreThisEvent = true;
            }
            #endregion

            return entry;
        }

        static void ProcessAbilityDamageType(this LogEntry entry)
        {
            var msg = entry.Message.ToUpper();
            // Physical Damage
            if (msg.Contains("PHYSICAL DAMAGE") || msg.Contains("PHYSISCH-SCHADEN") || msg.Contains("dégâts de Physiques"))
            {
                entry.AbilityDamageType = "Physical";
                return;
            }
            // Air damage
            if (msg.Contains("AIR DAMAGE") || msg.Contains("LUFT-SCHADEN") || msg.Contains("dégâts de Air"))
            {
                entry.AbilityDamageType = "Air";
                return;
            }
            // Water damage
            if (msg.Contains("WATER DAMAGE") || msg.Contains("WASSER-SCHADEN") || msg.Contains("dégâts de Eau"))
            {
                entry.AbilityDamageType = "Water";
                return;
            }
            // Earth damage
            if (msg.Contains("EARTH DAMAGE") || msg.Contains("ERDE-SCHADEN") || msg.Contains("dégâts de Terre"))
            {
                entry.AbilityDamageType = "Earth";
                return;
            }
            // Fire damage
            if (msg.Contains("FIRE DAMAGE") || msg.Contains("FEUER-SCHADEN") || msg.Contains("dégâts de Feu"))
            {
                entry.AbilityDamageType = "Fire";
                return;
            }
            // Life damage
            if (msg.Contains("LIFE DAMAGE") || msg.Contains("LEBEN-SCHADEN") || msg.Contains("dégâts de Vie"))
            {
                entry.AbilityDamageType = "Life";
                return;
            }
            // Death damage
            if (msg.Contains("DEATH DAMAGE") || msg.Contains("TOD-SCHADEN") || msg.Contains("dégâts de Mort"))
            {
                entry.AbilityDamageType = "Death";
                return;
            }
            // Ethereal damage
            if (msg.Contains("ETHEREAL DAMAGE") || msg.Contains("ÄTHERISCH-SCHADEN") || msg.Contains("dégâts de éthéré"))
            {
                entry.AbilityDamageType = "Ethereal";
                return;
            }
        }

        static void ProcessSpecial(this LogEntry entry)
        {
            int messageOpenBracket = entry.Message.IndexOf('(');
            int messageCloseBracket = entry.Message.IndexOf(')');
            if (messageOpenBracket >= 0 && messageCloseBracket >= 0)
            {
                string specialData = entry.Message.Substring(messageOpenBracket + 1,
                    messageCloseBracket - messageOpenBracket - 1)
                    .Replace("absorbiert", "ABSORBED").Replace("absorbed", "ABSORBED").Replace("absorbé", "ABSORBED") //Absorption
                    .Replace("geblockt", "BLOCKED").Replace("blocked", "BLOCKED").Replace("bloqué", "BLOCKED") // Blocked
                    .Replace("überheilen", "OVERHEAL").Replace("overheal", "OVERHEAL").Replace("excès de soins", "OVERHEAL") // Overheal
                    .Replace("abgefangen", "INTERCEPTED").Replace("intercepted", "INTERCEPTED").Replace("intercepté", "INTERCEPTED") // Intercepted
                    .Replace("ignoriert", "IGNORED").Replace("ignored", "IGNORED").Replace("ignoré", "IGNORED") // Ignored
                    .Replace("zu viel des Guten", "OVERKILL").Replace("overkill", "OVERKILL").Replace("surpuissance", "OVERKILL"); //Overkill 

                string[] strArray = specialData.Trim().Split(' ');
                if (strArray.Length != 0)
                {
                    if ((strArray.Length % 2) != 0)
                    {
                        return;
                    }
                    for (int i = 1; i <= (strArray.Length / 2); i++)
                    {
                        // In French combat logs, we might see 'Attaque auto. (à distance)', ranged auto attack.
                        // Use TryParse for the special, so that it doesn't break if it finds these lines
                        if (!Int64.TryParse(strArray[(i - 1) * 2].Trim(), out var num2)) continue;
                        string special = strArray[(i * 2) - 1].Trim();
                        switch (special)
                        {
                            case "ABSORBED":
                                entry.AbsorbedAmount = num2;
                                break;
                            case "BLOCKED":
                                entry.BlockedAmount = num2;
                                break;
                            case "OVERHEAL":
                                entry.OverhealAmount = num2;
                                break;
                            case "INTERCEPTED":
                                entry.InterceptAmount = num2;
                                break;
                            case "OVERKILL":
                                entry.OverKillAmount = num2;
                                break;
                            case "IGNORED":
                                entry.IgnoredAmount = num2;
                                break;
                            case "deflected": // should this even appear in lines anymore?
                                entry.DeflectAmount = num2;
                                break;
                            default:
                                Console.WriteLine("Found an unhandled special: {0}", special);
                                Console.WriteLine("Whole line: {0}", specialData);
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// This is the method that's called to remove commas from any abilities that contain them.
        /// We shouldn't have to do this but someone decided that commas in names wouldn't cause issues with CSV-formatted data. Nice.
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        static string ReplaceInvalidAbilityNames(this string input)
        {
            input = input
                .Replace("Saute, cours, vole !", "Juke and Run")
                .Replace("Blessing of Mobility, ", "Blessing of Mobility and ");

            return input;
        }

        static CharacterType GetCharacterType(string characterIdEntry, out string characterId)
        {
            characterId = "0";
            CharacterType returnValue = CharacterType.Unknown;

            // We expect the whole field here, e.g. T=P#R=R#240379631105751000
            try
            {
                string[] data = characterIdEntry.Split('#');
                if (data.Length == 3)
                {
                    // This is a player T=P#R=R#240379631105751000
                    // This is a pet    T=N#R=R#9223372045572243803
                    string attType = data[0].Substring(2, 1).ToUpper();
                    if (attType == "C" || attType == "P")
                    {
                        // C = Character who gathered the combatlog
                        // P = Another player in the group / raid
                        // O = Player outside the raid, e.g. someone who has just left the group
                        returnValue = CharacterType.Player;
                    }
                    else if (attType == "N")
                    {
                        // Check the relationship to the character gathering the combatlog
                        string relType = data[1].Substring(2, 1).ToUpper();
                        // G = Pet in raid (e.g. Beacon of Despair)
                        // R = Pet in raid (e.g. Blood Raptor)
                        // O = Outside of raid (NPC)
                        if (relType == "O")
                        {
                            returnValue = CharacterType.Npc;
                        }
                        else
                        {
                            returnValue = CharacterType.Pet;
                        }
                        //returnValue = relType == "O" ? CharacterType.NPC : CharacterType.Pet;
                    }
                    characterId = data[2];
                }
            }
            catch (Exception ex)
            {
                characterId = "0";
            }
            return returnValue;
        }

        public static bool CreateEncounterContainers(string parentPath, int encounterNumber)
        {
            var encounterPath = Path.Combine(parentPath, encounterNumber.ToString());
            try
            {
                // Encounter folder
                if (!Directory.Exists(encounterPath))
                {
                    Directory.CreateDirectory(encounterPath);
                }

                foreach (var container in encounterContainers)
                {
                    var thisPath = Path.Combine(encounterPath, container.Value);
                    var fs = File.Create(thisPath);
                    fs.Close();
                }

                // Double-check that they all exist
                foreach (var container in encounterContainers)
                {
                    var thisPath = Path.Combine(encounterPath, container.Value);
                    if (!File.Exists(thisPath))
                    {
                        Console.WriteLine($"Unable to find {thisPath}");
                        return false;
                    }
                }

                // All is good
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Checks whether the specified folder exists. If it doesn't, it will be created.
        /// </summary>
        /// <param name="folderPath"></param>
        /// <returns>True if the folder already existed or was successfully created, otherwise False.</returns>
        public static bool CheckFolderExists(string folderPath)
        {
            try
            {
                if (Directory.Exists(folderPath))
                {
                    return true;
                }

                // Create the directory for this session
                Directory.CreateDirectory(folderPath);

                // Check that it actually exists now and didn't silently fail
                return Directory.Exists(folderPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
        }

        private static int CountOccurrences(string input, char searchFor)
        {
            int count = 0;
            char[] inputChars = input.ToCharArray();
            int length = inputChars.Length;
            for (int n = 0; n < length; n++)
            {
                if (inputChars[n] == searchFor)
                    count++;
            }

            return count;
        }

        static bool ShouldStartCombat(this LogEntry entry)
        {
            switch (entry.AttackerType)
            {
                // Return true if a player is getting hit by an NPC, or an NPC is getting hit by a player.
                case CharacterType.Npc when entry.TargetType == CharacterType.Player && entry.IsDamageType:
                case CharacterType.Player when entry.TargetType == CharacterType.Npc && entry.IsDamageType:
                    return true;
                default:
                    return false;
            }
        }

        static void SetTimeElapsed(this LogEntry entry, DateTime encounterStart)
        {
            entry.SecondsElapsed = (int) (entry.CalculatedTimeStamp - encounterStart).TotalSeconds;
        }

        static int GetDowntimeValueForEncounter(this LogEntry entry)
        {
            foreach (var downtimeOverride in DowntimeOverrides)
            {
                if (entry.TargetType == CharacterType.Npc && entry.TargetName == downtimeOverride.NpcName)
                {
                    return downtimeOverride.DowntimeSeconds;
                }
                if (entry.AttackerType == CharacterType.Npc && entry.AttackerName == downtimeOverride.NpcName)
                {
                    return downtimeOverride.DowntimeSeconds;
                }
            }

            return DefaultEncounterDowntime;
        }

        /// <summary>
        /// This is only used to append a single line at a time. Works, but is incredibly slow. On the upside, uses next to no memory to function well.
        /// </summary>
        /// <param name="sessionPath"></param>
        /// <param name="encounterNumber"></param>
        /// <param name="filename"></param>
        /// <param name="line"></param>
        /// <returns></returns>
        static async Task AppendLine(string sessionPath, int encounterNumber, string filename, string line)
        {
            try
            {
                var filePath = Path.Combine(sessionPath, encounterNumber.ToString(), filename);
                await File.AppendAllTextAsync(filePath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing to {filename}: {ex.Message}");
            }
        }

        static async Task WriteEncounterInfo(string sessionPath, int encounterNumber, List<string> lines)
        {
            try
            {
                var filePath = Path.Combine(sessionPath, $"{encounterNumber}.txt");
                await using StreamWriter sw = new StreamWriter(filePath);
                foreach (var line in lines)
                {
                    await sw.WriteLineAsync(line);
                }

                await sw.FlushAsync();
                sw.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing to encounter {encounterNumber} info: {ex.Message}");
            }
        }

        static bool RemoveEncounterFolder(string sessionPath, int encounterNumber)
        {
            try
            {
                var encounterPath = Path.Combine(sessionPath, encounterNumber.ToString());
                if (Directory.Exists(encounterPath))
                {
                    Directory.Delete(encounterPath);
                }

                // Check again to ensure it's gone
                return !Directory.Exists(encounterPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error removing encounter {encounterNumber}: {ex.Message}");
                return false;
            }
        }
    }
}

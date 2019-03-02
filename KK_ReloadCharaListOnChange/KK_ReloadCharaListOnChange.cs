﻿using BepInEx;
using BepInEx.Logging;
using ChaCustom;
using Harmony;
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine.SceneManagement;
using Timer = System.Timers.Timer;

namespace KK_ReloadCharaListOnChange
{
    /// <summary>
    /// Watches the character folders for changes and updates the character/coordinate list in the chara maker and studio.
    /// </summary>
    [BepInDependency("com.bepis.bepinex.sideloader")]
    [BepInPlugin(GUID, PluginName, Version)]
    public class KK_ReloadCharaListOnChange : BaseUnityPlugin
    {
        public const string GUID = "com.deathweasel.bepinex.reloadcharalistonchange";
        public const string PluginName = "Reload Character List On Change";
        public const string Version = "1.4";
        private static FileSystemWatcher CharacterCardWatcher;
        private static FileSystemWatcher CoordinateCardWatcher;
        private static FileSystemWatcher StudioFemaleCardWatcher;
        private static FileSystemWatcher StudioMaleCardWatcher;
        private static FileSystemWatcher StudioCoordinateCardWatcher;
        private static readonly string FemaleCardPath = Path.Combine(Paths.GameRootPath, "UserData\\chara\\female");
        private static readonly string MaleCardPath = Path.Combine(Paths.GameRootPath, "UserData\\chara\\male");
        private static readonly string CoordinateCardPath = Path.Combine(Paths.GameRootPath, "UserData\\coordinate");
        private static bool DoRefresh = false;
        private static bool EventFromCharaMaker = false;
        private static bool InCharaMaker = false;
        private static CustomCharaFile CustomCharaFileInstance;
        private static CustomCoordinateFile CustomCoordinateFileInstance;
        private static Studio.CharaList StudioFemaleListInstance;
        private static Studio.CharaList StudioMaleListInstance;
        private static object StudioCoordinateListInstance;
        private static Timer CardTimer;
        private static CardEventType EventType;
        private static readonly ReaderWriterLockSlim rwlock = new ReaderWriterLockSlim();

        private static object ExtendedSaveInstance;
        private static Version LoadedVersionNumber;

        public void Main()
        {
            SceneManager.sceneUnloaded += SceneUnloaded;

            var harmony = HarmonyInstance.Create(GUID);
            harmony.PatchAll(typeof(KK_ReloadCharaListOnChange));
            harmony.Patch(typeof(Studio.MPCharCtrl).GetNestedType("CostumeInfo", BindingFlags.NonPublic).GetMethod("InitFileList", AccessTools.all),
                          new HarmonyMethod(typeof(KK_ReloadCharaListOnChange).GetMethod(nameof(StudioCoordinateListPrefix), AccessTools.all)), null);

            ExtendedSaveInstance = GetComponent<ExtensibleSaveFormat.ExtendedSave>();
            LoadedVersionNumber = MetadataHelper.GetMetadata(ExtendedSaveInstance).Version;
        }
        /// <summary>
        /// When cards are added or removed from the folder set a flag
        /// </summary>
        private static void CardEvent(CardEventType eventType)
        {
            //Needs to be locked since dumping a bunch of cards in the folder will trigger this event a whole bunch of times that all run at once
            rwlock.EnterWriteLock();

            try
            {
                EventType = eventType;

                //Start a timer which will be reset every time a card is added/removed for when the user dumps in a whole bunch at once
                //Once the timer elapses, a flag will be set to do the refresh, which will then happen on the next Update.
                if (CardTimer == null)
                {
                    //First file, start timer
                    CardTimer = new Timer(1000);
                    CardTimer.Elapsed += (o, ee) => DoRefresh = true;
                    CardTimer.Start();
                }
                else
                {
                    //Subsequent file, reset timer
                    CardTimer.Stop();
                    CardTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, ex);
                CardTimer?.Dispose();
            }

            rwlock.ExitWriteLock();
        }
        /// <summary>
        /// Get or set the value that determines whether card load events fire.
        /// It's different depending on the BepisPlugins version so we use reflection to get it depending on the version the user is running.
        /// </summary>
        private static bool LoadEvents
        {
            get
            {
                if (LoadedVersionNumber.Major <= 8 && LoadedVersionNumber.MajorRevision <= 0 && LoadedVersionNumber.Minor <= 0 && LoadedVersionNumber.MinorRevision <= 0)
                    return (bool)typeof(Sideloader.AutoResolver.Hooks).GetProperty("IsResolving").GetValue(null, null);
                else
                    return (bool)Traverse.Create(ExtendedSaveInstance).Field("LoadEventsEnabled").GetValue();
            }
            set
            {
                //Version 8.0 and below
                if (LoadedVersionNumber.Major <= 8 && LoadedVersionNumber.MajorRevision <= 0 && LoadedVersionNumber.Minor <= 0 && LoadedVersionNumber.MinorRevision <= 0)
                    typeof(Sideloader.AutoResolver.Hooks).GetProperty("IsResolving").SetValue(null, value, null);
                else//Version 8.0.0.1 and above
                    Traverse.Create(ExtendedSaveInstance).Field("LoadEventsEnabled").SetValue(value);
            }
        }
        /// <summary>
        /// Refresh the list
        /// </summary>
        private static void RefreshList()
        {
            try
            {
                //Turn off resolving to prevent spam since modded stuff isn't relevent for making this list.
                LoadEvents = false;
                switch (EventType)
                {
                    case CardEventType.CharaMakerCharacter:
                        typeof(CustomCharaFile).GetMethod("Initialize", AccessTools.all)?.Invoke(CustomCharaFileInstance, null);
                        break;
                    case CardEventType.CharaMakerCoordinate:
                        typeof(CustomCoordinateFile).GetMethod("Initialize", AccessTools.all)?.Invoke(CustomCoordinateFileInstance, null);
                        break;
                    case CardEventType.StudioFemale:
                        StudioFemaleListInstance.InitCharaList(true);
                        break;
                    case CardEventType.StudioMale:
                        StudioMaleListInstance.InitCharaList(true);
                        break;
                    case CardEventType.StudioCoordinate:
                        var sex = Traverse.Create(StudioCoordinateListInstance).Field("sex").GetValue();
                        typeof(Studio.MPCharCtrl).GetNestedType("CostumeInfo", BindingFlags.NonPublic).GetMethod("InitList", AccessTools.all)?.Invoke(StudioCoordinateListInstance, new object[] { 100 });
                        Traverse.Create(StudioCoordinateListInstance).Field("sex").SetValue(sex);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error | LogLevel.Message, "An error occured attempting to refresh the list.");
                Logger.Log(LogLevel.Error, $"KK_ReloadCharaListOnChange error: {ex.Message}");
                Logger.Log(LogLevel.Error, ex);
            }
            finally
            {
                LoadEvents = true;
            }
        }
        /// <summary>
        /// On a game update run the actual refresh. It must be run from an update or it causes all sorts of errors.
        /// </summary>
        private void Update()
        {
            if (EventFromCharaMaker && DoRefresh)
            {
                //If we saved or deleted a card from the chara maker itself there's no need to refresh, the game will handle that.
                CardTimer.Dispose();
                EventFromCharaMaker = false;
                DoRefresh = false;
            }
            else if (DoRefresh)
            {
                RefreshList();
                CardTimer.Dispose();
                DoRefresh = false;
            }
        }
        /// <summary>
        /// End the file watcher and set variables back to default for next time the chara maker is started
        /// </summary>
        private void SceneUnloaded(Scene s)
        {
            if (s.name == "CustomScene")
            {
                InCharaMaker = false;
                DoRefresh = false;
                EventFromCharaMaker = false;
                CardTimer?.Dispose();
                CardTimer = null;
                CharacterCardWatcher?.Dispose();
                CharacterCardWatcher = null;
                CoordinateCardWatcher?.Dispose();
                CoordinateCardWatcher = null;
            }
        }
        /// <summary>
        /// When saving a new character card in game set a flag
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaFileControl), "SaveCharaFile", new[] { typeof(BinaryWriter), typeof(bool) })]
        public static void SaveCharaFilePrefix()
        {
            if (InCharaMaker && Singleton<CustomBase>.Instance.customCtrl.saveNew == true)
                EventFromCharaMaker = true;
        }
        /// <summary>
        /// When saving a new coordinate card in game set a flag
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaFileCoordinate), nameof(ChaFileCoordinate.SaveFile), new[] { typeof(string) })]
        public static void SaveCoordinateFilePrefix(string path)
        {
            if (InCharaMaker && !File.Exists(path))
                EventFromCharaMaker = true;
        }
        /// <summary>
        /// When deleting a chara card in game set a flag
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CustomCharaFile), "DeleteCharaFile")]
        public static void DeleteCharaFilePrefix()
        {
            if (InCharaMaker)
                EventFromCharaMaker = true;
        }
        /// <summary>
        /// When deleting a coordinate card in game set a flag
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CustomCoordinateFile), "DeleteCoordinateFile")]
        public static void DeleteCoordinateFilePrefix()
        {
            if (InCharaMaker)
                EventFromCharaMaker = true;
        }
        /// <summary>
        /// Initialize the character card file watcher when the chara maker starts
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CustomCharaFile), "Initialize")]
        public static void CustomCharaFileInitializePrefix(CustomCharaFile __instance)
        {
            if (CharacterCardWatcher == null)
            {
                CustomCharaFileInstance = __instance;

                CharacterCardWatcher = new FileSystemWatcher();
                CharacterCardWatcher.Path = Singleton<CustomBase>.Instance.modeSex == 0 ? MaleCardPath : FemaleCardPath;
                CharacterCardWatcher.NotifyFilter = NotifyFilters.FileName;
                CharacterCardWatcher.Filter = "*.png";
                CharacterCardWatcher.EnableRaisingEvents = true;
                CharacterCardWatcher.Created += (o, ee) => CardEvent(CardEventType.CharaMakerCharacter);
                CharacterCardWatcher.Deleted += (o, ee) => CardEvent(CardEventType.CharaMakerCharacter);
                CharacterCardWatcher.IncludeSubdirectories = true;
            }

            InCharaMaker = true;
        }
        /// <summary>
        /// Initialize the coordinate card file watcher when the chara maker starts
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CustomCoordinateFile), "Initialize")]
        public static void CustomCoordinateFileInitializePrefix(CustomCoordinateFile __instance)
        {
            if (CoordinateCardWatcher == null)
            {
                CustomCoordinateFileInstance = __instance;

                CoordinateCardWatcher = new FileSystemWatcher();
                CoordinateCardWatcher.Path = CoordinateCardPath;
                CoordinateCardWatcher.NotifyFilter = NotifyFilters.FileName;
                CoordinateCardWatcher.Filter = "*.png";
                CoordinateCardWatcher.EnableRaisingEvents = true;
                CoordinateCardWatcher.Created += (o, ee) => CardEvent(CardEventType.CharaMakerCoordinate);
                CoordinateCardWatcher.Deleted += (o, ee) => CardEvent(CardEventType.CharaMakerCoordinate);
                CoordinateCardWatcher.IncludeSubdirectories = true;
            }
        }
        /// <summary>
        /// Initialize the file watcher once the list has been initiated
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(typeof(Studio.CharaList), "InitFemaleList")]
        public static void StudioFemaleListPrefix(Studio.CharaList __instance)
        {
            if (StudioFemaleCardWatcher == null)
            {
                StudioFemaleListInstance = __instance;

                StudioFemaleCardWatcher = new FileSystemWatcher();
                StudioFemaleCardWatcher.Path = FemaleCardPath;
                StudioFemaleCardWatcher.NotifyFilter = NotifyFilters.FileName;
                StudioFemaleCardWatcher.Filter = "*.png";
                StudioFemaleCardWatcher.EnableRaisingEvents = true;
                StudioFemaleCardWatcher.Created += (o, ee) => CardEvent(CardEventType.StudioFemale);
                StudioFemaleCardWatcher.Deleted += (o, ee) => CardEvent(CardEventType.StudioFemale);
                StudioFemaleCardWatcher.IncludeSubdirectories = true;
            }
        }
        /// <summary>
        /// Initialize the file watcher once the list has been initiated
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(typeof(Studio.CharaList), "InitMaleList")]
        public static void StudioMaleListPrefix(Studio.CharaList __instance)
        {
            if (StudioMaleCardWatcher == null)
            {
                StudioMaleListInstance = __instance;

                StudioMaleCardWatcher = new FileSystemWatcher();
                StudioMaleCardWatcher.Path = MaleCardPath;
                StudioMaleCardWatcher.NotifyFilter = NotifyFilters.FileName;
                StudioMaleCardWatcher.Filter = "*.png";
                StudioMaleCardWatcher.EnableRaisingEvents = true;
                StudioMaleCardWatcher.Created += (o, ee) => CardEvent(CardEventType.StudioMale);
                StudioMaleCardWatcher.Deleted += (o, ee) => CardEvent(CardEventType.StudioMale);
                StudioMaleCardWatcher.IncludeSubdirectories = true;
            }
        }
        /// <summary>
        /// Initialize the file watcher once the list has been initiated
        /// </summary>
        public static void StudioCoordinateListPrefix(object __instance)
        {
            if (StudioCoordinateCardWatcher == null)
            {
                StudioCoordinateListInstance = __instance;

                StudioCoordinateCardWatcher = new FileSystemWatcher();
                StudioCoordinateCardWatcher.Path = CoordinateCardPath;
                StudioCoordinateCardWatcher.NotifyFilter = NotifyFilters.FileName;
                StudioCoordinateCardWatcher.Filter = "*.png";
                StudioCoordinateCardWatcher.EnableRaisingEvents = true;
                StudioCoordinateCardWatcher.Created += (o, ee) => CardEvent(CardEventType.StudioCoordinate);
                StudioCoordinateCardWatcher.Deleted += (o, ee) => CardEvent(CardEventType.StudioCoordinate);
                StudioCoordinateCardWatcher.IncludeSubdirectories = true;
            }
        }

        public enum CardEventType { CharaMakerCharacter, CharaMakerCoordinate, StudioMale, StudioFemale, StudioCoordinate }
    }
}

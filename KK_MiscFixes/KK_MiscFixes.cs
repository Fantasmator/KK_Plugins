﻿using ActionGame;
using BepInEx;
using BepInEx.Logging;
using ChaCustom;
using FreeH;
using Harmony;
using Illusion.Game;
using Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using UniRx;
using UnityEngine;
using UnityEngine.UI;
using static ExtensibleSaveFormat.ExtendedSave;
using Logger = BepInEx.Logger;

namespace KK_MiscFixes
{
    [BepInPlugin(GUID, PluginName, Version)]
    public class KK_MiscFixes : BaseUnityPlugin
    {
        public const string GUID = "com.deathweasel.bepinex.miscfixes";
        public const string PluginName = "Misc Fixes";
        public const string PluginNameInternal = "KK_MiscFixes";
        public const string Version = "1.0";

        void Main()
        {
            var harmony = HarmonyInstance.Create(GUID);
            harmony.PatchAll(typeof(KK_MiscFixes));
        }
        /// <summary>
        /// Turn off Extended Save events
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(typeof(FreeHClassRoomCharaFile), "Start")]
        public static void StartPrefix(FreeHClassRoomCharaFile __instance) => LoadEventsEnabled = false;
        /// <summary>
        /// Turn back on Extended Save events, load a copy of the character with extended data on this time, and use that instead.
        /// </summary>
        [HarmonyPostfix, HarmonyPatch(typeof(FreeHClassRoomCharaFile), "Start")]
        public static void StartPostfix(FreeHClassRoomCharaFile __instance)
        {
            LoadEventsEnabled = true;

            ReactiveProperty<ChaFileControl> info = Traverse.Create(__instance).Field("info").GetValue<ReactiveProperty<ChaFileControl>>();
            ReactiveProperty<SaveData.Heroine> _heroine = (ReactiveProperty<SaveData.Heroine>)Singleton<FreeHCharaSelect>.Instance.GetType().GetField("_heroine", AccessTools.all).GetValue(Singleton<FreeHCharaSelect>.Instance);
            ReactiveProperty<SaveData.Player> _player = (ReactiveProperty<SaveData.Player>)Singleton<FreeHCharaSelect>.Instance.GetType().GetField("_player", AccessTools.all).GetValue(Singleton<FreeHCharaSelect>.Instance);
            Button enterButton = Traverse.Create(__instance).Field("enterButton").GetValue<Button>();

            enterButton.onClick.RemoveAllListeners();
            enterButton.OnClickAsObservable().Subscribe(delegate (Unit _)
            {
                ChaFileControl chaFileControl = new ChaFileControl();

                chaFileControl.LoadCharaFile(info.Value.charaFileName, info.Value.parameter.sex, false, true);

                if (__instance.sex == 0)
                    _player.Value = new SaveData.Player(chaFileControl, false);
                else
                    _heroine.Value = new SaveData.Heroine(chaFileControl, false);

                Singleton<Scene>.Instance.UnLoad();
            });
        }
    }
}
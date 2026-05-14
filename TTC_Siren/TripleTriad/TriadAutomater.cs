using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using ECommons.Automation;
using static ECommons.GenericHelpers;
using static TriadBuddyPlugin.UIReaderTriadGame;

namespace TTC;

internal static unsafe class TriadAutomater {

	public static bool ModuleEnabled = false;

	public static bool PlayXTimes = false;
	public static bool PlayUntilCardDrops = false;
	public static int NumberOfTimes = 1;
	public static bool LogOutAfterCompletion = false;
	public static bool PlayUntilAllCardsDropOnce = false;

	public static void PlaceCard(int which, int slot) {
		Plugin.Chat.Print($"放置卡牌: {which} 到位置: {slot}");
		try {
			if (TryGetAddonByName("TripleTriad", out AddonTripleTriad* addon)) {
				Callback.Fire(&addon->AtkUnitBase, true, 14, (uint)slot + ((uint)which << 16));
				addon->AtkUnitBase.Update(0);
				addon->TurnState = 0;
			} else {
				Plugin.Chat.PrintError("无法找到 TripleTriad UI 界面");
			}
		} catch (Exception ex) {
			Plugin.Chat.PrintError($"放置卡牌失败: {ex.Message}");
		}
	}
}
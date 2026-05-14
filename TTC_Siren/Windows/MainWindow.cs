using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.GameFunctions;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFTriadBuddy;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TriadBuddyPlugin;
using TTC;
using TtcServer;
using TtcServer.config;
using static TTC.Plugin;
using static TtcServer.Utils;
using FfxivAddonTripleTriad = FFXIVClientStructs.FFXIV.Client.UI.AddonTripleTriad;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using Plugin = TTC.Plugin;
using TtcCallback = ECommons.Automation.Callback;

public class MainWindow : Window {
	public MainWindow() : base("TTC_Siren") {
		SizeConstraints = new WindowSizeConstraints {
			MinimumSize = new Vector2(800, 600),
			MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
		};
	}

	public unsafe void Dispose() {
		var test2 = (AtkUnitBase*)GameGui.GetAddonByName("Social").Address;
		if (test2 != null) {
			test2->Scale = 1;
			test2->GetNodeById(1)->ScaleX = 1;
			test2->GetNodeById(1)->ScaleY = 1;
			test2->Close(true);
		}
		if (cancellationTokenSource is { IsCancellationRequested: false }) { cancellationTokenSource.Cancel(); }

		// 重置自动AI状态
		autoAiEnabled = false;
		aiRequestInProgress = false;
		autoExecuteAfterAi = false;
		isMyTurnPrevious = false;

		ClearTriadStatus();
	}

	private string customName = "";
	private string customWorld = "";
	private string customMessage = "";
	private string customTargetName = "";
	private string 被搜索的人名 = "";
	private bool isAutoSearchEnabled = false;
	private float targetX = 0f;
	private float targetY = 0f;
	private float targetZ = 0f;
	private bool lockX = false;
	private bool lockY = false;
	private bool lockZ = false;
	private bool firstclose = false;
	private XivChatType customType = XivChatType.Debug;
	// Dictionary to store locked positions for multiple targets
	private Dictionary<ulong, (float X, float Y, float Z, bool LockX, bool LockY, bool LockZ)> lockedTargets = new();
	private ChatHelper chatHelper = new();
	private static CancellationTokenSource cancellationTokenSource;
	private UIReaderTriadGame uiReaderGame = new();
	private UIReaderTriadPrep uiReaderPrep = new();
	private UIReaderTriadResults uiReaderResults = new();
	private string triadGameState = "未知";
	private string triadRules = "未知";
	private string triadCards = "未知";
	private string triadProgress = "未知";
	private string triadNpc = "未知";
	private string triadResult = "未知";
	private UIReaderTriadCardList uiReaderCardList = new();
	private UIReaderTriadDeckEdit uiReaderDeckEdit = new();
	private string triadCardListInfo = "未知";
	private string triadDeckEditInfo = "未知";
	private int lastPlaced = -1; // 记录上一次的已下卡数
	private int myOwner; // 1=蓝方, 2=红方
	private string myName = "";
	private string blueName = "";
	private string redName = "";
	private bool shouldOutputBoard;
	private string aiResponse = "";
	private Task? lastAiTask;

	private string blue_time = "--:--";
	private string red_time = "--:--";
	// 手动设置己方手牌可用状态（用于秩序/混乱规则）
	private bool[] myHandCanUse = [true, true, true, true, true]; // 默认5张牌都可用
	private bool showHandSelector;

	// 自动AI对局相关变量
	private bool autoAiEnabled;
	private bool isMyTurnPrevious; // 记录上一帧是否是我的回合
	private bool aiRequestInProgress; // 防止重复请求
	private bool autoExecuteAfterAi; // 标记是否需要在AI响应后自动执行

	// 1. 定义AI返回结构
	//迁移到Server


	public static unsafe bool TryGetAddonByName<T>(string Addon, out T* AddonPtr) where T : unmanaged {
		var a = GameGui.GetAddonByName(Addon);
		if (a.IsNull) {
			AddonPtr = null;
			return false;
		}
		AddonPtr = (T*)a.Address;
		return true;
	}


	private static string FormatCard(UIStateTriadCard? card) {
		if (card == null) return "empty";
		if (!card.isPresent) return "empty";
		var value = $"[{card.numU:X}-{card.numR:X}-{card.numD:X}-{card.numL:X}], owner:{card.owner}";
		if (card is { numU: 0, numR: 0, numD: 0, numL: 0, owner: 0 })
			return "[暗牌]";
		return value;
	}

	private static void ShowDeck(string title, UIStateTriadCard[]? deck, int owner) {
		ImGui.TextColored(owner == 1 ? new Vector4(0.2f, 0.8f, 1f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f), title);
		if (deck == null) {
			ImGui.Text("无数据");
			return;
		}
		for (var i = 0; i < deck.Length; i++) {
			var card = deck[i];
			var text = $"{i + 1}. {FormatCard(card)}";
			if (card is not { isPresent: true }) {
				ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), text + " (已上场)");
			} else if (card is { numU: 0, numR: 0, numD: 0, numL: 0, owner: 0 }) {
				ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), text + " (暗牌)");
			} else {
				ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), text);
			}
		}
	}

	private static void ShowBoard(UIStateTriadCard[]? board) {
		ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "棋盘：");
		if (board == null) {
			ImGui.Text("无数据");
			return;
		}
		for (var row = 0; row < 3; row++) {
			var line = "";
			for (var col = 0; col < 3; col++) {
				var idx = row * 3 + col;
				var card = board[idx];
				if (card == null || !card.isPresent)
					line += "[ - ] ";
				else if (card is { numU: 0, numR: 0, numD: 0, numL: 0, owner: 0 })
					line += "[暗] ";
				else
					line += $"[{card.numU:X}-{card.numR:X}-{card.numD:X}-{card.numL:X}] ";
			}
			ImGui.Text(line);
		}
	}

	private static unsafe void InteractObject(IGameObject obj) {
		if (TargetSystem.Instance()->Target == (GameObject*)obj.Address) {
			TargetSystem.Instance()->InteractWithObject((GameObject*)obj.Address);
			return;
		}
		TargetSystem.Instance()->Target = (GameObject*)obj.Address;
	}

	private static unsafe void ListenBattle(IFramework framework) {
		// 100552998 objectid

		foreach (var player in ObjectTable) {
			if (player.ObjectKind != ObjectKind.EventNpc) continue;


			if (player.DataId == 1011061) {
				var obj = player.Struct();

				if (obj->NamePlateIconId == 61721 && !Condition[ConditionFlag.InDutyQueue]) {
					InteractObject(player);
					InteractObject(player);

					TryClickSelectString("申请参赛报名");
					TryClickSelectYesno();
				}
			}
		}

		// Use direct GameGui service to get addon
		var addonPtrSelect = GameGui.GetAddonByName("TripleTriadPickUpDeckSelect");
		if (!addonPtrSelect.IsNull) {
			var addon = (FfxivAddonTripleTriad*)addonPtrSelect.Address;
			TtcCallback.Fire(&addon->AtkUnitBase, true, 1);
			TtcCallback.Fire(&addon->AtkUnitBase, true, 2);
		}
		var addonPtrC = GameGui.GetAddonByName("TripleTriadDeckConfirmation");
		if (!addonPtrC.IsNull) {
			var addon = (FfxivAddonTripleTriad*)addonPtrC.Address;
			TtcCallback.Fire(&addon->AtkUnitBase, true, 0);
		}
		var addonPtrr = GameGui.GetAddonByName("TripleTriadTournamentResult");
		if (!addonPtrr.IsNull) {
			var addon = (FfxivAddonTripleTriad*)addonPtrr.Address;
			TtcCallback.Fire(&addon->AtkUnitBase, true, 2, false);
			TtcCallback.Fire(&addon->AtkUnitBase, true, 0, false);
		}
		var addonPtrre = GameGui.GetAddonByName("TripleTriadTournamentReport");
		if (!addonPtrre.IsNull) {
			var addon = (FfxivAddonTripleTriad*)addonPtrre.Address;
			TtcCallback.Fire(&addon->AtkUnitBase, true, 0, false);
		}
		var addonPtrrew = GameGui.GetAddonByName("TripleTriadTournamentReward");
		if (!addonPtrrew.IsNull) {
			var addon = (FfxivAddonTripleTriad*)addonPtrrew.Address;
			TtcCallback.Fire(&addon->AtkUnitBase, true, 0, false);
		}
	}

	private bool autorece = false;
	private bool autorece2 = false;


	private static bool TryClickSelectString(string text) {
		var addonWrapper = GameGui.GetAddonByName("SelectString");
		if (addonWrapper.IsNull) {
			return false;
		}

		var addon = new AddonMaster.SelectString(addonWrapper.Address);
		foreach (var entry in addon.Entries) {
			if (entry.Text.Contains(text, StringComparison.OrdinalIgnoreCase)) {
				entry.Select();
				return true;
			}
		}

		return false;
	}

	private static void TryClickSelectYesno() {
		var addonWrapper = GameGui.GetAddonByName("SelectYesno");
		if (addonWrapper.IsNull)  return; 
		new AddonMaster.SelectYesno(addonWrapper.Address).Yes();
	}


	public override void Draw() {
		// 1. 自动获取UI指针并调用UIReader
		// 对局中
		var gameAddonPtr = GameGui.GetAddonByName("TripleTriad");
		if (gameAddonPtr != IntPtr.Zero)
			uiReaderGame.OnAddonUpdate(gameAddonPtr);
		// 赛前准备
		var prepAddonPtr = GameGui.GetAddonByName("TripleTriadRequest");
		if (prepAddonPtr != IntPtr.Zero)
			uiReaderPrep.OnAddonUpdateMatchRequest(prepAddonPtr);
		var deckSelectAddonPtr = GameGui.GetAddonByName("TripleTriadSelDeck");
		if (deckSelectAddonPtr != IntPtr.Zero)
			uiReaderPrep.OnAddonUpdateDeckSelect(deckSelectAddonPtr);
		// 赛果
		var resultAddonPtr = GameGui.GetAddonByName("TripleTriadResult");
		if (resultAddonPtr != IntPtr.Zero)
			uiReaderResults.OnAddonUpdate(resultAddonPtr);
		// 卡牌列表
		var cardListAddonPtr = GameGui.GetAddonByName("GSInfoCardList");
		if (cardListAddonPtr != IntPtr.Zero)
			uiReaderCardList.OnAddonUpdate(cardListAddonPtr);
		// 卡组编辑
		var deckEditAddonPtr = GameGui.GetAddonByName("GSInfoEditDeck");
		if (deckEditAddonPtr != IntPtr.Zero)
			uiReaderDeckEdit.OnAddonUpdate(deckEditAddonPtr);

		// 2. 解析状态
		UpdateTriadBuddyStatus();

		// 3. ImGui展示 - 使用标准表格布局
		// 创建两列布局
		ImGui.Columns(2, "MainColumns");

		// === 标题行 ===
		if (ImGui.Button("重置设置")) {
			Plugin.Configuration.UnknownCardConfig = new UnknownCardConfig();
			Plugin.Configuration.Save();
		}
		ImGui.TextColored(new Vector4(1, 1, 0, 1), "幻卡对局状态监控");
		ImGui.NextColumn();
		ImGui.PushFont(UiBuilder.IconFont);
		ImGui.TextColored(new Vector4(0.2f, 1f, 1f, 1f), FontAwesomeIcon.Crosshairs.ToIconString());
		ImGui.PopFont();
		ImGui.SameLine();
		ImGui.TextColored(new Vector4(0.2f, 1f, 1f, 1f), " AI控制面板");
		ImGui.NextColumn();

		// === 分隔线 ===
		ImGui.Separator();
		ImGui.NextColumn();
		ImGui.Separator();
		ImGui.NextColumn();

		// === 第一列：游戏状态信息 ===
		ImGui.Text($"对局状态: {triadGameState}");
		ImGui.Text($"规则: {triadRules}");
		ImGui.Text($"对手: {triadNpc}");
		ImGui.Spacing();
		ImGui.Text("[U-R-D-L]");
		ShowDeck($"蓝方卡组({blueName})：", uiReaderGame.currentState?.blueDeck, 1);
		ShowDeck($"红方卡组({redName})：", uiReaderGame.currentState?.redDeck, 2);
		ShowBoard(uiReaderGame.currentState?.board);
		ImGui.Text($"进度: {triadProgress}");
		ImGui.Text($"赛果: {triadResult}");
		ImGui.Spacing();
		ImGui.Text($"卡牌列表: {triadCardListInfo}");
		ImGui.Text($"卡组编辑: {triadDeckEditInfo}");

		ImGui.Text($"红方时间: {red_time}");
		ImGui.Text($"蓝方时间: {blue_time}");

		if (myOwner == 1 && blue_time != "--:--")
			ImGui.Text($"到我的回合");
		if (myOwner == 2 && red_time != "--:--")
			ImGui.Text($"到我的回合");
		if (myOwner != 1 && myOwner != 2) ImGui.Text($"玩家位置未知");
		if (Condition[ConditionFlag.InDutyQueue])
			ImGui.Text("排本中");

		// === 第二列：AI控制和分析 ===
		ImGui.NextColumn();
		if (aiRequestInProgress) ImGui.Text($"AI思考中...");
		else if (ImGui.Button("获取AI分析")) {
			shouldOutputBoard = true;
			autoExecuteAfterAi = false; // 手动请求不自动执行
		}

		ImGui.SameLine();
		if (ImGui.Button("手牌设置")) showHandSelector = !showHandSelector;
		ImGui.Spacing();

		// 自动AI开关
		var autoButtonColor = autoAiEnabled ? new Vector4(0.2f, 1f, 0.2f, .5f) : new Vector4(0.8f, 0.8f, 0.8f, .5f);
		var autoButtonText = autoAiEnabled ? "自动锦标赛已启用" : "启用自动锦标赛";
		ImGui.PushStyleColor(ImGuiCol.Button, autoButtonColor);
		if (ImGui.Button(autoButtonText, new Vector2(200, 35))) {
			autoAiEnabled = !autoAiEnabled;
			if (autoAiEnabled) {
				Framework.Update += ListenBattle;
				Log.Info("自动AI对局已启用");
				aiResponse = ""; // 清空之前的响应
				isMyTurnPrevious = false; // 重置状态
				aiRequestInProgress = false;
			} else {
				Framework.Update -= ListenBattle;
				Log.Info("自动AI对局已关闭");
				aiRequestInProgress = false;
				autoExecuteAfterAi = false;
			}
		}
		ImGui.PopStyleColor();

		if (autoAiEnabled) {
			ImGui.SameLine();
			var statusColor = aiRequestInProgress ? new Vector4(1f, 0.8f, 0.2f, 1f) : new Vector4(0.2f, 1f, 0.2f, 1f);
			var statusText = aiRequestInProgress ? "🧠 AI思考中..." : "⏳ 等待轮次";
			ImGui.TextColored(statusColor, statusText);

			// 显示额外的状态信息
			ImGui.Spacing();
			ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), $"自动AI状态:");
			ImGui.Text($"  当前玩家: {(myOwner == 1 ? "蓝方" : myOwner == 2 ? "红方" : "未知")}");

			var isMyTurnNow = myOwner == 1 && blue_time != "--:--" || myOwner == 2 && red_time != "--:--";
			var turnColor = isMyTurnNow ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(0.8f, 0.8f, 0.8f, 1f);
			ImGui.TextColored(turnColor, $"  轮次状态: {(isMyTurnNow ? "我的回合" : "等待中")}");

			if (lastAiTask != null) {
				var taskColor = lastAiTask.IsCompleted ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(1f, 0.8f, 0.2f, 1f);
				var taskStatus = lastAiTask.IsCompleted ? "已完成" : lastAiTask.IsFaulted ? "出错" : "进行中";
				ImGui.TextColored(taskColor, $"  AI任务: {taskStatus}");
			}
		} else {
			ImGui.Text("请开启 DR 以下功能：");
			ImGui.Text("自动跳过动画 -AutoTalkSkip");
			ImGui.Text("自动任务出发确认 -AutoCommenceDuty");
		}

		// 显示手牌可用性设置面板
		if (showHandSelector) {
			DrawHandSelector();
		}

		ImGui.Spacing();

		if (!string.IsNullOrWhiteSpace(aiResponse)) {
			ImGui.PushFont(UiBuilder.IconFont);
			ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), FontAwesomeIcon.Robot.ToIconString());
			ImGui.PopFont();
			ImGui.SameLine();
			ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), " AI分析结果");
			ImGui.Separator();

			try {
				var aiResult = JsonSerializer.Deserialize<AiMoveResponse>(aiResponse);
				if (aiResult != null) {
					// 1. 推荐出牌
					ImGui.PushFont(UiBuilder.IconFont);
					ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), FontAwesomeIcon.Clipboard.ToIconString());
					ImGui.PopFont();
					ImGui.SameLine();
					ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), " 推荐出牌:");
					var previewCardTexture = aiResult.card_id > 0 ? GetCardTexture(aiResult.card_id) : null;
					var previewBackground = previewCardTexture != null ? GetCardBackgroundImage() : null;
					var previewSize = new Vector2(78, 96);

					ImGui.BeginGroup();
					DrawCardPreview(previewBackground, previewCardTexture, previewSize);
					ImGui.EndGroup();
					ImGui.SameLine();

					ImGui.BeginGroup();
					ImGui.Text($"   卡牌: {GetCardDisplayName(aiResult.card_id, aiResult.card)}");
					ImGui.Text($"   卡牌ID: {aiResult.card_id}");
					ImGui.Text($"   位置: {FormatBoardPosition(aiResult.pos)}");
					ImGui.Text($"   棋盘索引: {GetBoardIndexFromPosition(aiResult.pos)}");
					ImGui.Spacing();
					if (ImGui.Button("执行推荐", new Vector2(120, 30))) {
						ExecuteAIMove(aiResult);
					}
					ImGui.EndGroup();

					// 2. 胜率分析
					if (aiResult.win_probability != null) {
						ImGui.Spacing();
						ImGui.PushFont(UiBuilder.IconFont);
						ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), FontAwesomeIcon.ChartLine.ToIconString());
						ImGui.PopFont();
						ImGui.SameLine();
						ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), " 胜率分析:");
						ImGui.Text($"   当前胜率: {aiResult.win_probability.current:P1}");
						ImGui.Text($"   出牌后胜率: {aiResult.win_probability.after_move:P1}");
						var winRateChange = aiResult.win_probability.after_move - aiResult.win_probability.current;
						var changeColor = winRateChange > 0 ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f);
						ImGui.SameLine();
						ImGui.TextColored(changeColor, $"({(winRateChange > 0 ? "+" : "")}{winRateChange:P1})");
						ImGui.Text($"   预测置信度: {aiResult.win_probability.confidence:P1}");
					}

					// 3. 对手手牌分析
					if (aiResult.opponent_hand_analysis != null) {
						ImGui.Spacing();
						ImGui.PushFont(UiBuilder.IconFont);
						ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), FontAwesomeIcon.User.ToIconString());
						ImGui.PopFont();
						ImGui.SameLine();
						ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), " 对手分析:");
						ImGui.TextWrapped($"   策略预测: {aiResult.opponent_hand_analysis.strategy_analysis}");

						if (aiResult.opponent_hand_analysis.total_unknown > 0) {
							ImGui.Text($"   未知卡牌: {aiResult.opponent_hand_analysis.total_unknown}张");
						}

						if (aiResult.opponent_hand_analysis.predicted_cards is { Count: > 0 }) {
							// 分离已知卡牌和预测卡牌
							var knownCards = aiResult.opponent_hand_analysis.predicted_cards.Where(c => c.confidence >= 1.0).ToList();
							var predictedCards = aiResult.opponent_hand_analysis.predicted_cards.Where(c => c.confidence < 1.0).ToList();

							if (knownCards.Count > 0) {
								ImGui.Text("   已知手牌:");
								foreach (var card in knownCards.Take(3)) {
									ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), $"     ✓ {card.card} 星级:{card.star}");
									ImGui.TextWrapped($"       {card.reasoning}");
								}
							}

							if (predictedCards.Count > 0) {
								ImGui.Text("   预测手牌:");
								foreach (var card in predictedCards.Take(4)) {
									var confidenceColor = card.confidence > 0.7 ? new Vector4(0.2f, 1f, 0.2f, 1f) :
										card.confidence > 0.5 ? new Vector4(1f, 0.8f, 0.2f, 1f) :
										new Vector4(0.8f, 0.8f, 0.8f, 1f);
									var confidenceIcon = card.confidence > 0.7 ? "●" : card.confidence > 0.5 ? "◐" : "○";
									ImGui.TextColored(confidenceColor, $"     {confidenceIcon} {card.card} 星级:{card.star} ({card.confidence:P0})");
									ImGui.TextWrapped($"       {card.reasoning}");
								}
							}
						}
					}

					// 4. AI建议
					if (aiResult.recommendation != null) {
						ImGui.Spacing();
						ImGui.PushFont(UiBuilder.IconFont);
						ImGui.TextColored(new Vector4(0.8f, 1f, 0.2f, 1f), FontAwesomeIcon.Lightbulb.ToIconString());
						ImGui.PopFont();
						ImGui.SameLine();
						ImGui.TextColored(new Vector4(0.8f, 1f, 0.2f, 1f), " AI建议:");
						ImGui.TextWrapped($"   移动价值: {aiResult.recommendation.move_reasoning}");
						ImGui.TextWrapped($"   战略意义: {aiResult.recommendation.strategic_value}");

						if (aiResult.recommendation.alternative_moves is { Count: > 0 }) {
							ImGui.Text("   备选方案:");
							foreach (var alt in aiResult.recommendation.alternative_moves.Take(2)) // 减少显示数量
							{
								ImGui.TextWrapped($"     • {alt.card} 位置:[{string.Join(",", alt.pos ?? [])}] 评分:{alt.value:F2}");
							}
						}
					}
				} else {
					ImGui.TextWrapped(aiResponse);
				}
			} catch (Exception ex) {
				ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "解析AI响应时出错:");
				ImGui.TextWrapped($"{ex.Message}");
				ImGui.Text("原始响应:");
				ImGui.TextWrapped(aiResponse);
			}
		} else {
			ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "点击上方按钮获取AI分析");
		}

		// 恢复单列布局
		ImGui.Columns();
	}

	private unsafe void UpdateTriadBuddyStatus() {
		var test = (AtkUnitBase*)GameGui.GetAddonByName("TripleTriad").Address;

		if (test != null) {
			try {
				redName = test->GetTextNodeById(186)->GetAsAtkTextNode()->NodeText.GetText();
				blueName = test->GetTextNodeById(139)->GetAsAtkTextNode()->NodeText.GetText();
				blue_time = test->GetTextNodeById(128)->GetAsAtkTextNode()->NodeText.GetText();
				red_time = test->GetTextNodeById(170)->GetAsAtkTextNode()->NodeText.GetText();
				myName = ObjectTable.LocalPlayer?.Name.ToString() ?? "";
				if (myName != redName) myOwner = 1;
				else if (myName != blueName) myOwner = 2;

				//Plugin.Log.Debug($"Red Name: {redName}, Blue Name: {blueName}, My Name: {myName}, My Owner: {myOwner}");
				triadRules = string.Join(", ", new[] {
					test->GetTextNodeById(192)->GetAsAtkTextNode()->NodeText.GetText(),
					test->GetTextNodeById(193)->GetAsAtkTextNode()->NodeText.GetText(),
					test->GetTextNodeById(194)->GetAsAtkTextNode()->NodeText.GetText(),
					test->GetTextNodeById(195)->GetAsAtkTextNode()->NodeText.GetText()
				}.Where(text => !string.IsNullOrWhiteSpace(text)));
			} catch (Exception ex) {
				Log.Error(ex.ToString());
				return;
			}
			// 1. 赛前准备界面（如选卡组、规则、NPC）
			var prepState = uiReaderPrep.cachedState;
			triadNpc = string.IsNullOrEmpty(prepState.npc) ? "未知" : prepState.npc;

			// 2. 对局中
			var gameState = uiReaderGame.currentState;
			if (gameState == null) {
				ClearTriadStatus();
				return;
			}
			triadGameState = uiReaderGame.status.ToString();
			// 蓝方卡组
			var blueCards = gameState.blueDeck != null
				? string.Join(" | ", gameState.blueDeck.Select(c => c?.ToString() ?? "空"))
				: "未知";
			// 红方卡组
			var redCards = gameState.redDeck != null
				? string.Join(" | ", gameState.redDeck.Select(c => c?.ToString() ?? "空"))
				: "未知";
			// 棋盘
			var boardCards = gameState.board != null
				? string.Join(" | ", gameState.board.Select(c => c?.ToString() ?? "空"))
				: "未知";
			triadCards = $"蓝方卡组: {blueCards}\n红方卡组: {redCards}\n棋盘: {boardCards}";
			// 进度
			var placed = gameState.board?.Count(c => c is { isPresent: true }) ?? 0;
			triadProgress = $"已下卡数: {placed}";

			// 只有点击按钮时才输出一次当前牌局信息
			if (shouldOutputBoard) {
				// fire and forget
				aiRequestInProgress = true;
				lastAiTask = OutputGameStateToJsonAsync(gameState, myOwner);
				shouldOutputBoard = false;
			}

			// 自动AI对局逻辑
			CheckAndTriggerAutoAI(gameState);
			// 3. 赛果
			triadResult = GetResultState();

			// 4. 卡牌列表信息
			var cardListState = uiReaderCardList.cachedState;
			triadCardListInfo = $"当前卡牌: U:{cardListState.numU} L:{cardListState.numL} D:{cardListState.numD} R:{cardListState.numR} 稀有度:{cardListState.rarity} 类型:{cardListState.type}";
			// 5. 卡组编辑信息
			var deckEditState = uiReaderDeckEdit.cachedState;
			triadDeckEditInfo = $"卡组编辑页: {deckEditState.pageIndex}, 选中卡: {deckEditState.cardIndex}";
		}
	}

	private void ClearTriadStatus() {
		triadGameState = "未知";
		triadRules = "未知";
		triadCards = "未知";
		triadProgress = "未知";
		triadNpc = "未知";
		triadResult = "未知";
		triadCardListInfo = "未知";
		triadDeckEditInfo = "未知";
	}

	private string GetResultState() {
		// 反射获取UIReaderTriadResults的私有cachedState
		var result = uiReaderResults.GetType().GetField("cachedState", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(uiReaderResults);
		if (result == null) return "未知";
		var numMGP = (int)result.GetType().GetField("numMGP")?.GetValue(result);
		var isWin = (bool)result.GetType().GetField("isWin")?.GetValue(result);
		var isDraw = (bool)result.GetType().GetField("isDraw")?.GetValue(result);
		var isLose = (bool)result.GetType().GetField("isLose")?.GetValue(result);
		var cardItemId = (uint)result.GetType().GetField("cardItemId")?.GetValue(result);
		if (isWin) return $"胜利！奖励MGP: {numMGP}, 卡牌ID: {cardItemId}";
		if (isDraw) return $"平局，奖励MGP: {numMGP}";
		if (isLose) return $"失败，奖励MGP: {numMGP}";
		return "未知";
	}

	private void DrawHandSelector() {
		ImGui.Separator();
		ImGui.PushFont(UiBuilder.IconFont);
		ImGui.TextColored(new Vector4(0.8f, 1f, 0.2f, 1f), FontAwesomeIcon.HandPointer.ToIconString());
		ImGui.PopFont();
		ImGui.SameLine();
		ImGui.TextColored(new Vector4(0.8f, 1f, 0.2f, 1f), " 己方手牌可用性设置");
		ImGui.Text("（用于秩序/混乱规则下手动指定可用卡牌）");
		ImGui.Spacing();

		// 获取当前己方手牌信息用于显示
		var gameState = uiReaderGame.currentState;
		if (gameState != null) {
			var myDeck = myOwner == 1 ? gameState.blueDeck : gameState.redDeck;
			if (myDeck != null) {
				// 确保数组大小匹配实际手牌数量
				var deckSize = myDeck.Length;
				if (myHandCanUse.Length != deckSize) {
					var newArray = new bool[deckSize];
					for (var i = 0; i < Math.Min(myHandCanUse.Length, deckSize); i++) {
						newArray[i] = myHandCanUse[i];
					}
					for (var i = myHandCanUse.Length; i < deckSize; i++) {
						newArray[i] = true; // 新卡牌默认可用
					}
					myHandCanUse = newArray;
				}

				// 显示每张手牌的选择框
				for (var i = 0; i < myDeck.Length; i++) {
					var card = myDeck[i];
					if (card is { isPresent: true }) {
						var cardInfo = $"[{card.numU:X}-{card.numR:X}-{card.numD:X}-{card.numL:X}]";

						var canUse = i < myHandCanUse.Length ? myHandCanUse[i] : true;
						if (ImGui.Checkbox($"##card{i}", ref canUse)) {
							if (i < myHandCanUse.Length) {
								myHandCanUse[i] = canUse;
							}
						}
						ImGui.SameLine();

						var textColor = canUse ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(0.6f, 0.6f, 0.6f, 1f);
						ImGui.TextColored(textColor, $"卡牌 {i + 1}: {cardInfo} {(canUse ? "✓ 可用" : "✗ 不可用")}");
					} else {
						ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"卡牌 {i + 1}: [已使用]");
					}
				}

				ImGui.Spacing();

				// 快捷操作按钮
				if (ImGui.Button("全部可用")) {
					for (var i = 0; i < myHandCanUse.Length; i++) {
						myHandCanUse[i] = true;
					}
				}
				ImGui.SameLine();
				if (ImGui.Button("全部不可用")) {
					for (var i = 0; i < myHandCanUse.Length; i++) {
						myHandCanUse[i] = false;
					}
				}
				ImGui.SameLine();
				if (ImGui.Button("重置默认")) {
					for (var i = 0; i < myHandCanUse.Length; i++) {
						myHandCanUse[i] = true;
					}
				}
			} else {
				ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "无手牌数据");
			}
		} else {
			ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "未在对局中");
		}

		ImGui.Separator();
	}

	private static string DecodeUnicode(string input) {
		return Regex.Replace(
			input,
			@"\\u([0-9A-Fa-f]{4})",
			m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()
		);
	}

	private static IDalamudTextureWrap? GetCardTexture(int cardId) {
		var iconId = TriadCardDB.GetCardTextureId(cardId);
		var resource = TextureProvider.GetFromGameIcon(new GameIconLookup(iconId));
		if (resource != null && resource.TryGetWrap(out var result, out _)) {
			return result;
		}

		return null;
	}

	private static IDalamudTextureWrap? GetCardBackgroundImage() {
		var resource = TextureProvider.GetFromGame("ui/uld/CardTripleTriad.tex");
		if (resource != null && resource.TryGetWrap(out var result, out _)) {
			return result;
		}

		return null;
	}

	private static void DrawCardPreview(IDalamudTextureWrap? background, IDalamudTextureWrap? card, Vector2 size) {
		var startPos = ImGui.GetCursorScreenPos();
		if (background != null) {
			var uv1 = background.Height > 0 ? new Vector2(1.0f, size.Y / background.Height) : Vector2.One;
			ImGui.Image(background.Handle, size, Vector2.Zero, uv1);
		} else {
			ImGui.Dummy(size);
		}

		if (card != null) {
			ImGui.SetCursorScreenPos(startPos);
			ImGui.Image(card.Handle, size);
		}
	}

	private static string GetCardDisplayName(int cardId, string fallback) {
		if (cardId > 0) {
			var cardOb = TriadCardDB.Get().FindById(cardId);
			if (cardOb != null) {
				return cardOb.Name.GetLocalized();
			}
		}

		return string.IsNullOrWhiteSpace(fallback) ? "未知卡牌" : fallback;
	}

	private int GetCardIndexFromCardId(int cardId) {
		if (cardId <= 0) {
			return -1;
		}

		var cardOb = TriadCardDB.Get().FindById(cardId);
		var gameState = uiReaderGame.currentState;
		if (cardOb == null || gameState == null) {
			return -1;
		}

		var myDeck = myOwner == 1 ? gameState.blueDeck : gameState.redDeck;
		if (myDeck == null) {
			return -1;
		}

		var targetU = (byte)cardOb.Sides[(int)ETriadGameSide.Up];
		var targetL = (byte)cardOb.Sides[(int)ETriadGameSide.Left];
		var targetD = (byte)cardOb.Sides[(int)ETriadGameSide.Down];
		var targetR = (byte)cardOb.Sides[(int)ETriadGameSide.Right];

		for (var i = 0; i < myDeck.Length; i++) {
			var card = myDeck[i];
			if (card is { isPresent: true } &&
			    card.numU == targetU &&
			    card.numL == targetL &&
			    card.numD == targetD &&
			    card.numR == targetR) {
				return i;
			}
		}

		return -1;
	}

	// 新增：将AI推荐的卡牌名称转换为手牌索引
	private int GetCardIndexFromName(string cardName) {
		// 将AI返回的数字10替换为十六进制A，以匹配游戏显示格式
		cardName = cardName.Replace("10", "A");

		var gameState = uiReaderGame.currentState;
		if (gameState == null) return -1;

		var myDeck = myOwner == 1 ? gameState.blueDeck : gameState.redDeck;
		if (myDeck == null) return -1;

		// 尝试通过卡牌数值匹配
		for (var i = 0; i < myDeck.Length; i++) {
			var card = myDeck[i];
			if (card is { isPresent: true }) {
				// AI返回的格式是 U R D L，但UI显示的是 U L D R
				// 所以我们需要同时检查两种格式

				// AI格式：[U-R-D-L]
				var aiPattern = $"[{card.numU:X}-{card.numR:X}-{card.numD:X}-{card.numL:X}]";
				var aiPatternNoDelim = $"{card.numU:X}-{card.numR:X}-{card.numD:X}-{card.numL:X}";

				// UI格式：[U-L-D-R] 
				var uiPattern = $"[{card.numU:X}-{card.numL:X}-{card.numD:X}-{card.numR:X}]";
				var uiPatternNoDelim = $"{card.numU:X}-{card.numL:X}-{card.numD:X}-{card.numR:X}";

				// 检查AI格式匹配
				if (cardName.Contains(aiPattern) || cardName.Contains(aiPatternNoDelim) ||
				    cardName.Contains(uiPattern) || cardName.Contains(uiPatternNoDelim)) {
					Log.Debug($"找到匹配卡牌 {i + 1}: {cardName} -> [{card.numU:X}-{card.numR:X}-{card.numD:X}-{card.numL:X}]");
					return i;
				}

				// 如果AI返回的是完整的卡牌描述，尝试更宽松的匹配
				if (cardName.Contains($"{card.numU:X}") && cardName.Contains($"{card.numR:X}") &&
				    cardName.Contains($"{card.numD:X}") && cardName.Contains($"{card.numL:X}")) {
					Log.Debug($"通过数值匹配找到卡牌 {i + 1}: {cardName}");
					return i;
				}
			}
		}

		Log.Warning($"无法找到匹配的卡牌: {cardName}");

		// 调试信息：打印所有可用卡牌
		Log.Debug("当前可用手牌:");
		for (var i = 0; i < myDeck.Length; i++) {
			var card = myDeck[i];
			if (card is { isPresent: true }) {
				Log.Debug($"  卡牌 {i + 1}: [{card.numU:X}-{card.numR:X}-{card.numD:X}-{card.numL:X}]");
			}
		}

		return -1;
	}

	private int ResolveCardIndex(AiMoveResponse aiResult) {
		if (aiResult.card_id > 0) {
			var cardIndex = GetCardIndexFromCardId(aiResult.card_id);
			if (cardIndex >= 0) {
				return cardIndex;
			}
		}

		return GetCardIndexFromName(aiResult.card ?? string.Empty);
	}

	// 新增：将AI返回的坐标转换为棋盘位置索引
	private static int GetBoardIndexFromPosition(int[] pos) {
		if (pos == null || pos.Length != 2) return -1;

		var row = pos[0];
		var col = pos[1];

		// 验证坐标范围
		if (row < 0 || row > 2 || col < 0 || col > 2) return -1;

		// 转换为线性索引：row * 3 + col
		return row * 3 + col;
	}

	private static string FormatBoardPosition(int[] pos) {
		if (pos == null || pos.Length != 2) {
			return "未知位置";
		}

		var row = pos[0];
		var col = pos[1];
		if (row < 0 || row > 2 || col < 0 || col > 2) {
			return $"无效位置[{string.Join(",", pos)}]";
		}

		return $"第{row + 1}行，第{col + 1}列";
	}

	// 新增：执行AI推荐的卡牌放置
	private void ExecuteAIMove(AiMoveResponse aiResult) {
		try {
			if (aiResult == null) {
				return;
			}

			var cardIndex = ResolveCardIndex(aiResult);
			var boardIndex = GetBoardIndexFromPosition(aiResult.pos);
			var displayName = GetCardDisplayName(aiResult.card_id, aiResult.card ?? string.Empty);
			var boardText = FormatBoardPosition(aiResult.pos);

			if (cardIndex == -1) {
				Log.Error($"无法找到卡牌: {displayName}");
				return;
			}

			if (boardIndex == -1) {
				Log.Error($"无效的棋盘位置: {boardText}");
				return;
			}

			// 检查卡牌是否可用
			if (cardIndex < myHandCanUse.Length && !myHandCanUse[cardIndex]) {
				Log.Warning($"卡牌 {cardIndex + 1} 当前不可用（被秩序/混乱规则限制）");
				return;
			}

			Log.Info($"执行AI推荐: 放置卡牌 {cardIndex + 1} ({displayName}) 到位置 {boardText} (索引: {boardIndex})");

			// 调用放置卡牌方法
			TriadAutomater.PlaceCard(cardIndex, boardIndex);
		} catch (Exception ex) {
			Log.Error($"执行AI移动时出错: {ex.Message}");
		}
	}

	private void ExecuteAIMove(string cardName, int[] position) {
		ExecuteAIMove(new AiMoveResponse {
			card = cardName,
			pos = position,
			card_id = -1
		});
	}

	// 检查并触发自动AI
	private void CheckAndTriggerAutoAI(UIStateTriadGame gameState) {
		if (!autoAiEnabled || gameState == null || myOwner == 0) return;

		// 检测是否轮到我的回合
		var isMyTurnNow = myOwner == 1 && blue_time != "--:--" || myOwner == 2 && red_time != "--:--";

		// 只有在刚刚轮到我的回合时才触发（从非我的回合转为我的回合）
		if (isMyTurnNow && !isMyTurnPrevious && !aiRequestInProgress) {
			Log.Info($"检测到轮到我的回合，触发自动AI请求 (我是{(myOwner == 1 ? "蓝方" : "红方")})");

			// 检查是否还有可用手牌
			var myDeck = myOwner == 1 ? gameState.blueDeck : gameState.redDeck;
			var hasAvailableCards = false;
			if (myDeck != null) {
				for (var i = 0; i < myDeck.Length; i++) {
					var card = myDeck[i];
					var canUse = i < myHandCanUse.Length ? myHandCanUse[i] : true;
					if (card is { isPresent: true } && canUse) {
						hasAvailableCards = true;
						break;
					}
				}
			}

			if (hasAvailableCards) {
				aiRequestInProgress = true;
				autoExecuteAfterAi = true; // 标记自动执行
				aiResponse = ""; // 清空之前的响应

				// 触发AI请求
				lastAiTask = OutputGameStateToJsonAsync(gameState, myOwner);
				Log.Info("自动AI请求已发送");
			} else {
				Log.Warning("没有可用的手牌，跳过AI请求");
			}
		}

		// 更新回合状态
		isMyTurnPrevious = isMyTurnNow;
	}

	private async Task OutputGameStateToJsonAsync(UIStateTriadGame gameState, int myOwner) {
		// 棋盘
		var board = new List<object>();
		for (var i = 0; i < 9; i++) {
			var card = gameState.board[i];
			var row = i / 3; // 行：上到下 0~2
			var col = i % 3; // 列：左到右 0~2
			if (card is { isPresent: true }) {
				board.Add(new {
					pos = new[] { row, col }, // [row, col]，与Python Board类一致
					card.numU, card.numR, card.numD, card.numL, card.owner
				});
			}
		}
		// 我的手牌
		var myHand = new List<object>();
		var oppHand = new List<object>();
		var myDeck = myOwner == 1 ? gameState.blueDeck : gameState.redDeck;
		var oppDeck = myOwner == 1 ? gameState.redDeck : gameState.blueDeck;

		for (var originalIndex = 0; originalIndex < myDeck.Length; originalIndex++) {
			var card = myDeck[originalIndex];
			if (card is { isPresent: true }) {
				// 获取该卡牌的可用状态，使用原始索引
				var canUse = originalIndex < myHandCanUse.Length ? myHandCanUse[originalIndex] : true;

				myHand.Add(new {
					card.numU, card.numR, card.numD, card.numL, canUse // 新增：是否可用
				});
			}
		}
		foreach (var card in oppDeck) {
			if (card is { isPresent: true }) {
				oppHand.Add(new {
					card.numU, card.numR, card.numD, card.numL,
					canUse = true // 对手手牌默认可用（我们不知道对手的秩序/混乱限制）
				});
			}
		}
		var currentPlayer = 0;
		if (blue_time != "--:--") currentPlayer = 1;
		else if (red_time != "--:--") currentPlayer = 2;

		var jsonObj = new {
			board, myHand, oppHand, myOwner, currentPlayer,
			rules = triadRules
		};
		var options = new JsonSerializerOptions {
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};
		var json = JsonSerializer.Serialize(jsonObj, options);
		try {
			// 打印完整的JSON内容到日志
			Log.Debug($"发送到AI的数据:\n{json}");

			// using var client = new HttpClient();
			// var content = new StringContent(json, Encoding.UTF8, "application/json");
			// var resp = await client.PostAsync("http:                                                    //127.0.0.1:5000/ai_move", content);

			// var raw = await resp.Content.ReadAsStringAsync();
			// aiResponse = DecodeUnicode(raw);
			await Task.Run(() => aiResponse = JsonSerializer.Serialize(AiServer.ai_move(JsonSerializer.Deserialize<AiServer.PostJson>(json))));
			// 打印AI的响应
			Log.Debug($"AI的响应:\n{aiResponse}");

			// 如果是自动AI模式且需要自动执行，则执行AI推荐的移动
			if (autoExecuteAfterAi && autoAiEnabled) {
				try {
					var aiResult = JsonSerializer.Deserialize<AiMoveResponse>(aiResponse);
					if (aiResult is { pos: not null }) {
						Log.Info($"自动执行AI推荐: {aiResult.card} -> [{string.Join(",", aiResult.pos)}]");

						// 延迟一小段时间确保UI状态稳定
						await Task.Delay(500);

						ExecuteAIMove(aiResult);
					} else {
						Log.Warning("AI响应格式无效，无法自动执行");
					}
				} catch (Exception aiEx) {
					Log.Error($"自动执行AI推荐时出错: {aiEx.Message}");
				} finally {
					autoExecuteAfterAi = false; // 重置标志
				}
			}
		} catch (Exception ex) {
			aiResponse = $"[AI请求异常] {ex.Message}";
			Log.Error($"AI请求异常: {ex}");
		} finally {
			aiRequestInProgress = false; // 重置请求状态
		}
	}
}
using System;
using System.Collections.Generic;
using TtcServer.core;
using static TtcServer.ai.AI;
using static TtcServer.ai.MonteCarlo.MonteCarloSolver;
using static TtcServer.AiServer.ConsoleSearchReporter;
using static TtcServer.Utils;

namespace TtcServer;

public partial class AiServer {
	public static AiMoveResponse? ai_move(PostJson data) {
		HashSet<int> used_cards = [];
		var board = parse_board(data.board!);
		println("收到客户端消息的棋盘：");
		println(board.ToString());
		var my_owner = parse_owner(data.myOwner.Value);
		var opp_owner = my_owner == "red" ? "blue" : "red";
		println(data.oppHand.ToString());
		                          //先解析规则，然后用于智能手牌处理
		var (rules, open_mode) = parse_rules_and_open_mode(data.rules!);
		                          //选拔规则特殊处理：需要全局统计星级使用情况
		if (rules.Contains("选拔")) {
			println("检测到选拔规则，启动全局星级约束分析");
			                          //先统计棋盘和已知手牌的星级使用情况
			var global_star_usage = analyze_global_star_usage(board, data.myHand!, data.oppHand!);
			println($"全局星级使用情况: {global_star_usage}");
		}
		                          // 使用智能手牌解析，对对手手牌启用行为建模
		                          // 蒙特卡洛模式下牌采样（牌采样（求解器内部自行处理未知卡牌）
		var solver_type = data.solver ?? "minminimax";
		var mc_skip_sampling = solver_type == "monte_carlo";
		var my_hand = parse_hand(data.myHand!, my_owner, used_cards, rules, board, false);
		var opp_hand = parse_hand(data.oppHand!, opp_owner, used_cards, rules, board, true, skip_sampling: mc_skip_sampling);
		                          //玩家顺序：遵循GameState约定 - players[0]=红方, players[1]=蓝方
		                          // currentPlayer: 1=蓝方回合, 2=红方回合, 0=未知(兼容旧客户端)
		var current_player = data.currentPlayer ?? 0;
		                          //按照红蓝方约定创建玩家列表
		                          // 我是红方(players[0]), 对手是蓝方(players[1])
		// 对手是红方(players[0]), 我是蓝方(players[1])
		Player[] players = my_owner == "red" ? [new Player("me", my_hand), new Player("opp", opp_hand)] : [new Player("opp", opp_hand), new Player("me", my_hand)];
		//确定当前回合玩家
		int current_player_idx;
		if (current_player == 2) // 红方回合
			current_player_idx = 0;
		else if (current_player == 1) // 蓝方回合
			current_player_idx = 1;
		else {
			//兼容旧客户端：从棋盘卡牌数量推断回合
			var red_count = 0;
			var blue_count = 0;
			for (var r = 0; r < 3; r++) {
				for (var c = 0; c < 3; c++) {
					var board_card = board.get_card(r, c);
					if (board_card != null)
						if (board_card.owner == "red")
							red_count += 1;
						else if (board_card.owner == "blue")
							blue_count += 1;
				}
			}
			if (red_count < blue_count)
				current_player_idx = 0; // 红方落后, 红方回合
			else if (blue_count < red_count)
				current_player_idx = 1; // 蓝方落后, 蓝方回合
			else
				current_player_idx = 0; // 平局, 默认红方(先手)回合
		}
		var is_my_turn = my_owner == "red" && current_player_idx == 0 || my_owner == "blue" && current_player_idx == 1;
		println($"[Turn] my_owner={my_owner}, currentPlayer={current_player}, current_player_idx={current_player_idx}, is_my_turn={is_my_turn}");
		var game_state = new GameState(board, players, current_player_idx, rules);
		//如果有同类强化/弱化规则，立即处理
		if (rules.Contains("同类强化") || rules.Contains("同类弱化")) {
			game_state.recalculate_type_modifiers();
			println("应用同类规则后的类型分析：");
			_print_type_analysis(game_state);
		}
		//检查是否请求详细搜索进度 (默认关闭以提升性能)
		var show_search_progress = data.show_search_progress;
		List<SearchProgressData> search_progress_data = [];
		var console_reporter = new ConsoleSearchReporter(0.5f);
		var opp_unknown_count = _count_unknown_slots_from_hand(opp_hand);
		var use_endgame_robust = solver_type == "minimax" && _should_use_endgame_robust_mode(board, opp_unknown_count);


		//选择求解器（solver_type 已在上面读取）
		var mc_simulations = data.mc_simulations; // 蒙特卡洛模拟次数
		(Card, (int, int))? move;
		if (solver_type == "monte_carlo") {
			println($"[Solver] 使用蒙特卡洛求解器 (simulations={mc_simulations})");
			(move, _) = monte_carlo_best_move(
				game_state,
				get_all_cards(),
				my_owner,
				8,
				mc_simulations,
				true);
		} else {
			println("[Solver] 使用 Minimax 求解器");
			(move, _) = find_best_move_parallel(
				game_state,
				10,
				false,
				all_cards: get_all_cards(),
				open_mode: open_mode,
				max_time: 10,
				progress_callback: progress_callback);
			if (use_endgame_robust) {
				var opponent_player_idx = my_owner == "blue" ? 0 : 1;
				var scenario_sample_count = opp_unknown_count == 0 ? 1 : Math.Min(24, Math.Max(16, opp_unknown_count * 12));
				var scenario_states = _build_endgame_scenarios(
					game_state,
					opp_hand,
					used_cards,
					rules,
					board,
					opp_owner,
					opponent_player_idx,
					scenario_sample_count);
				var (robust_move, robust_candidates) = select_endgame_robust_move(
					game_state,
					scenario_states,
					game_state.current_player_idx,
					console_reporter);
				var robust_lookup = new Dictionary<(int, int, int), ScoredMoves>();
				foreach (var item in robust_candidates)
					robust_lookup[_move_key(item.move)] = item;
				if (move != null && robust_move != null) {
					var standard_item = robust_lookup[_move_key(move.Value)];
					var robust_item = robust_lookup[_move_key(robust_move.Value)];
					if (standard_item != null && robust_item != null) {
						var should_override = standard_item.safety_ratio < 0.5 && robust_item.safety_ratio >= 0.5 || robust_item.safety_ratio > standard_item.safety_ratio &&
							robust_item.final_score >= standard_item.final_score - 1.0 || robust_item.final_score > standard_item.final_score + 0.05;
						if (should_override && _move_key(robust_move.Value) != _move_key(move.Value)) {
							println(
								$"[Solver] 信息集残局覆盖: 标准={standard_item.final_score:.3f}/" +
								$"{standard_item.safety_ratio:.2f}, " +
								$"信息集={robust_item.final_score:.3f}/{robust_item.safety_ratio:.2f}, " +
								$"场景={scenario_states.Count}");
							move = robust_move;
						}
					}
				}
			}
		}
		if (move == null) return null;
		var (card, (row, col)) = move.Value;
		//分析对手手牌
		var opponent_analysis = analyze_opponent_hand(opp_hand, rules, board);
		//调试信息：输出对手手牌分析结果
		println($"对手手牌分析: 总计{opp_hand.Count}张卡牌, 其中{opponent_analysis.total_unknown}张未知");
		//计算胜率
		var win_prob = calculate_win_probability(game_state, move.Value, my_owner);
		//生成AI建议
		var recommendation = generate_move_recommendation(game_state, move.Value, my_owner);
		//打印AI给出结果后的棋盘状态
		var new_state = (GameState)game_state.Clone();
		new_state.play_move(row, col, card);
		println("AI给出结果后的棋盘：");
		println(new_state.board.ToString());
		//性能统计
		AiMoveResponse.PerformanceStats performance_stats = new() {
			nodes_searched = SEARCH_STATS.nodes_searched,
			search_depth = SEARCH_STATS.depth_completed,
			tt_hit_rate = SEARCH_STATS.tt_hits / Math.Max(SEARCH_STATS.nodes_searched, 1f) * 100,
			cutoff_rate = SEARCH_STATS.alpha_beta_cutoffs / Math.Max(SEARCH_STATS.nodes_searched, 1f) * 100,
			unknown_cards_processed = opponent_analysis.total_unknown,
			performance_optimizations_active = true // 标记优化已激活
		};
		println("性能统计:");
		println($"  搜索节点: {performance_stats.nodes_searched:,}");
		println($"  搜索深度: {performance_stats.search_depth}");
		println($"  置换表命中率: {performance_stats.tt_hit_rate:.1f}%");
		println($"  α-β剪枝率: {performance_stats.cutoff_rate:.1f}%");
		println($"  未知卡牌处理: {performance_stats.unknown_cards_processed} 张");

		var star_map = get_card_star_map();
		var star = star_map.TryGetValue(card.card_id, out var v) ? v.ToString() : "?";

		// 生成卡牌显示信息（包含原始和修正后的数值）
		var card_display = format_card_display(card, star);

		// 准备返回结果
		AiMoveResponse result = new() {
			card = card_display,
			card_id = card.card_id,
			pos = [row, col],
			opponent_hand_analysis = opponent_analysis,
			win_probability = win_prob,
			recommendation = recommendation,
			performance_stats = performance_stats // 添加性能统计
		};
		//如果请求了搜索进度，添加到结果中
		if (show_search_progress && search_progress_data != null) {
			result.search_progress = search_progress_data;
			result.search_summary = generate_search_summary(search_progress_data);
		}
		return result;

		void progress_callback(ProgressInfo progress_info) {
			//轻量级搜索进度回调函数
			console_reporter.on_minimax_progress(progress_info);
			//只在需要时才做复杂的数据处理
			if (show_search_progress) {
				search_progress_data.Add(new() {
					depth = progress_info.depth,
					max_depth = progress_info.max_depth,
					best_move = format_move_for_display(progress_info.best_move),
					best_score = progress_info.best_score,
					nodes_searched = progress_info.nodes_searched,
					time_elapsed = progress_info.time_elapsed,
					time_remaining = progress_info.time_remaining,
					nodes_per_second = progress_info.stats.nodes_per_second,
					tt_hit_rate = progress_info.stats.tt_hit_rate * 100,
					cutoff_rate = progress_info.stats.cutoff_rate * 100,
					branching_factor = progress_info.stats.avg_branching_factor
				});
				// 输出详细进度（仅在明确请求时）
				println(
					$"搜索进度更新 - 深度 {progress_info.depth}: " +
					$"评分={progress_info.best_score:.3f}, " +
					$"节点={progress_info.nodes_searched:,}, " +
					$"时间={progress_info.time_elapsed:.2f}秒");
			}
		}
	}

	public class SearchProgressData {
		public int depth;
		public int max_depth;
		public string best_move;
		public float best_score;
		public int nodes_searched;
		public float time_elapsed;
		public float time_remaining;
		public float nodes_per_second;
		public float tt_hit_rate;
		public float cutoff_rate;
		public float branching_factor;
	}

	// public static void Main() {
	// 	//清除之前的输出并显示字符画
	// 	println("title TTC_Siren - Triple Triad AI Server");
	// 	// 启动应用，使用threaded=True减少警告
	// 	println(" * Running on http://127.0.0.1:5000");
	// 	println("Press CTRL+C to quit");
	// 	var app = WebApplication.Create();
	// 	app.Urls.Add("http://127.0.0.1:5000");
	// 	//获取搜索进度的专用端点
	// 	app.MapPost("/search_progress", async _ => {
	// 		await Task.Run(() => {
	// 			try {
	// 				// 这里可以实现实时搜索进度查询
	// 				// 目前返回简单的状态信息
	// 				return Results.Ok(new {
	// 					status = "searching",
	// 					message = "搜索进行中，请等待完成"
	// 				});
	// 			} catch (Exception e) {
	// 				return Results.Problem(detail: e.Message, statusCode: StatusCodes.Status500InternalServerError);
	// 			}
	// 		});
	// 	});
	// 	app.MapPost("/ai_move", (PostJson? data) => {
	// 		try {
	// 			if (data == null ||
	// 			    data.board == null ||
	// 			    data.myHand == null ||
	// 			    data.oppHand == null ||
	// 			    data.myOwner == null) {
	// 				println( JsonSerializer.Serialize(data) );
	// 				return Results.BadRequest(new { error = "Invalid input data" });
	// 			}
	// 			var aiResult = ai_move(data);
	// 			if (aiResult == null)
	// 				return Results.Ok(new {
	// 					move = null as string,
	// 					msg = "无可用动作"
	// 				});
	// 			return Results.Ok(aiResult);
	// 		} catch (Exception e) {
	// 			println(e);
	// 			return Results.Problem(detail: e.Message, statusCode: StatusCodes.Status500InternalServerError);
	// 		}
	// 	});
	// 	app.Run();
	// }
}
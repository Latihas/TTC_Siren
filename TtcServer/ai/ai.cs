                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TtcServer.core;
using static TtcServer.AiServer.ConsoleSearchReporter;
using static TtcServer.Utils;

namespace TtcServer.ai;

public class AI {
	//角落策略评分的全局权重，可根据实际效果调节
	public const float CORNER_STRATEGY_WEIGHT = 5.0f;

	public class TranspositionTableEntry {
		public float depth;
		public float score;
		public (Card card, (int, int) pos)? move;
	}

	public static Dictionary<string, TranspositionTableEntry> TRANSPOSITION_TABLE = [];

	public static Dictionary<(int, int, int), int> HISTORY_TABLE = [];

	public static float TIME_LIMIT = 5.0f; //默认5秒时间限制
	public static DateTime START_TIME = DateTime.MinValue;
	public static DateTime PROGRESS_LAST_TIME = DateTime.MinValue;
	public static float PROGRESS_INTERVAL = 0.5f;

	public class SearchStats {
		public int nodes_searched;
		public int tt_hits;
		public int tt_cutoffs;
		public int alpha_beta_cutoffs;
		public int move_evaluations;
		public int depth_completed;
		public List<float> best_score_history = [];
		public List<object> best_move_history = [];
		public List<float> search_time_per_depth = [];
		public List<int> nodes_per_depth = [];
		public List<float> branching_factors = [];

		public SearchStats() {
			reset();
		}

		public void reset() {
			nodes_searched = 0;
			tt_hits = 0;
			tt_cutoffs = 0;
			alpha_beta_cutoffs = 0;
			move_evaluations = 0;
			depth_completed = 0;
			best_score_history = [];
			best_move_history = [];
			search_time_per_depth = [];
			nodes_per_depth = [];
			branching_factors = [];
		}

		public void add_depth_stats(int depth, int nodes, float time_taken, float best_score, object best_move, float branching_factor) {
			depth_completed = depth;
			nodes_per_depth.Add(nodes);
			search_time_per_depth.Add(time_taken);
			best_score_history.Add(best_score);
			best_move_history.Add(best_move);
			branching_factors.Add(branching_factor);
		}

		public class Summary {
			public int total_nodes;
			public float total_time;
			public float nodes_per_second;
			public float tt_hit_rate;
			public float cutoff_rate;
			public float avg_branching_factor;
			public int depths_completed;
			public List<float> score_trend;
		}

		public Summary get_summary() {
			var total_time = search_time_per_depth.Count == 0 ? 0 : search_time_per_depth.Aggregate((a, b) => a + b);
			var total_nodes = nodes_per_depth.Count == 0 ? 0 : nodes_per_depth.Aggregate((a, b) => a + b);
			var avg_branching = branching_factors.Count == 0 ? 0 : branching_factors.Aggregate((a, b) => a + b) / branching_factors.Count;
			return new Summary {
				total_nodes = total_nodes,
				total_time = total_time,
				nodes_per_second = total_nodes / total_time,
				tt_hit_rate = 1f * tt_hits / Math.Max(nodes_searched, 1),
				cutoff_rate = 1f * alpha_beta_cutoffs / Math.Max(nodes_searched, 1),
				avg_branching_factor = avg_branching,
				depths_completed = depth_completed,
				score_trend = best_score_history.Count >= 3 ? best_score_history.GetRange(best_score_history.Count - 3, 3) : best_score_history
			};
		}
	}

	public static SearchStats SEARCH_STATS = new();


	public class SearchResult(float eval_score, (Card card, (int, int) pos)? best_move, List<(Card, (int, int))>? path = null, SearchResult.SearchResultStat? stats = null) {
		public float eval_score = eval_score;
		public (Card card, (int, int) pos)? best_move = best_move;
		public List<(Card, (int, int))> path = path ?? [];
		public SearchResultStat stats = stats ?? new();

		public class SearchResultStat {
			public float branching_factor;
		}
	}

	// public string resource_path(string relative_path) => Path.Combine(AppContext.BaseDirectory, relative_path);

	private static Dictionary<int, int>? get_card_star_map_cache;

	public static Dictionary<int, int>? get_card_star_map() {
		if (get_card_star_map_cache == null
		    // && File.Exists(resource_path("data/幻卡数据库.csv"))
		   ) {
			get_card_star_map_cache = [];
			foreach (var row in AiServer.get_card_db()) {
				var id = Convert.ToInt32(row.Id);
				var star = Convert.ToInt32(row.Star);
				get_card_star_map_cache.Add(id, star);
			}
		}
		return get_card_star_map_cache;
	}

	//统计棋盘已占用格数。
	private static int _count_occupied_cells(GameState state) {
		var occupied = 0;
		for (var r = 0; r < 3; r++)
		for (var c = 0; c < 3; c++)
			if (state.board.get_card(r, c) != null)
				occupied++;
		return occupied;
	}

	//计算残局阶段的边值暴露惩罚
	private static float _calculate_endgame_exposure_penalty(Card card, int row, int col, GameState state) {
		var occupied = _count_occupied_cells(state);
		if (occupied < 5)
			return 0.0f;
		var stage_weight = 1.0f + MathF.Min((occupied - 4) / 4.0f, 1.0f);
		var penalty = 0.0f;
		(int, int, string, string)[] directions = [
			(-1, 0, "up", "down"),
			(1, 0, "down", "up"),
			(0, -1, "left", "right"),
			(0, 1, "right", "left")
		];
		var attackable_sides = 0;
		var weak_attackable_sides = 0;
		foreach (var (dr, dc, my_dir, opp_dir) in directions) {
			var nr = row + dr;
			var nc = col + dc;
			if (nr < 0 || nr >= 3 || nc < 0 || nc >= 3) continue;
			attackable_sides++;
			var my_value = card.get_effective_value(my_dir, state.rules);
			var adj_card = state.board.get_card(nr, nc);
			if (my_value <= 3) {
				weak_attackable_sides++;
				penalty += (4 - my_value) * 2.5f;
			} else if (my_value == 4)
				penalty += 1.0f;
			if (adj_card != null && adj_card.owner != card.owner) {
				var opp_value = adj_card.get_effective_value(opp_dir, state.rules);
				if (opp_value > my_value)
					penalty += (opp_value - my_value) * 1.8f;
				else if (opp_value == my_value)
					penalty += 1.0f;
			}
		}

		if (CORNER_SPAN.Contains((row, col)) && weak_attackable_sides > 0)
			penalty += weak_attackable_sides * 2.0f;
		else if (attackable_sides >= 3 && weak_attackable_sides > 0)
			penalty += weak_attackable_sides * 1.0f;
		return penalty * stage_weight;
	}

	//改进的评估函数
	public static float evaluate_state(GameState state, int ai_player_idx) {
		var (red_count, blue_count) = state.count_cards();
		//基础分数
		var base_score = ai_player_idx == 0 ? red_count - blue_count : blue_count - red_count;
		//位置权重
		Dictionary<(int, int), float> position_weights = new() {
			//角落
			{ (0, 0), 1.5f },
			{ (0, 2), 1.5f },
			{ (2, 0), 1.5f },
			{ (2, 2), 1.5f },
			//中心
			{ (1, 1), 1.2f }
		};
		var position_score = 0f;
		var corner_edge_score = 0.0f; // 新增：角落边值综合评分
		var handler = UnknownCardHandler.get_unknown_card_handler();
		for (var r = 0; r < 3; r++) {
			for (var c = 0; c < 3; c++) {
				var card = state.board.get_card(r, c);
				if (card != null) {
					var weight = position_weights.GetValueOrDefault((r, c), 1.0f);
					if (card.owner == "red" && ai_player_idx == 0 ||
					    card.owner == "blue" && ai_player_idx == 1) {
						position_score += weight; // 角落边值加分（仅己方卡牌）
						if (handler != null && CORNER_SPAN.Contains((r, c)))
							try {
								var cs = UnknownCardHandler._calculate_corner_strategy_score(card, state.board);
								corner_edge_score += cs * (CORNER_STRATEGY_WEIGHT * 0.6f); // 在总评估中权重稍低
							} catch {
								//
							}
					} else {
						position_score -= weight; // 对手角落高边则扣分
						if (handler != null && CORNER_SPAN.Contains((r, c)))
							try {
								var cs = UnknownCardHandler._calculate_corner_strategy_score(card, state.board);
								corner_edge_score -= cs * (CORNER_STRATEGY_WEIGHT * 0.6f);
							} catch {
								//
							}
					}
				}
			}
		}
		return base_score * 0.7f + position_score * 0.3f + corner_edge_score * 0.1f;
	}

/*
 * 高级评估函数数学模型说明：

1. 综合评分函数 (Comprehensive Evaluation Function)
\[
E_{total} = \alpha H + \beta P + \gamma C + \delta T + \epsilon N
\]
其中：
- H: 历史启发得分 (History Heuristic Score)
- P: 位置评估得分 (Position Evaluation Score)
- C: 卡牌属性得分 (Card Attribute Score)
- T: 战术评估得分 (Tactical Evaluation Score)
- N: 邻近协同得分 (Neighborhood Synergy Score)
权重系数：\alpha = 0.1, \beta = 1.5, \gamma = 2.0, \delta = 3.0, \epsilon = 1.0

2. 历史启发评分 (History Heuristic)
\[
H(m) = min(\frac{h(m)}{10}, 1000)
\]
其中 h(m) 为历史表中的原始值

3. 位置权重矩阵 (Position Weight Matrix)
\[
W = \begin{bmatrix}
1.5 & 1.0 & 1.5 \\
1.0 & 1.2 & 1.0 \\
1.5 & 1.0 & 1.5
\end{bmatrix}
\]

4. 卡牌战术价值 (Card Tactical Value)
\[
V_{card} = \sum_{i=1}^4 E_i + S \cdot M + \sum_{j=1}^k C_j
\]
其中：
- E_i: 边值评估 (Edge Values)
- S: 星级系数 (Star Rating)
- M: 位置乘数 (Position Multiplier)
- C_j: 组合加成 (Combination Bonus)

5. 吃子评估函数 (Capture Evaluation)
\[
Cap(x,y) = 30 + 5\Delta + 10(S_1 - S_2)
\]
其中：
- \Delta: 数值差异 (Value Difference)
- S_1: 己方星级 (Own Star Rating)
- S_2: 对方星级 (Opponent Star Rating)

6. 邻近协同系数 (Neighborhood Synergy)
\[
N(x,y) = \sum_{i,j \in Adj(x,y)} 10 \cdot I(owner_{i,j} = owner_{x,y})
\]
其中 I 为示性函数

7. 深度奖励因子 (Depth Reward Factor)
\[
R(d) = min(2^d, 1000000)
\]

8. 最终评分归一化 (Score Normalization)
\[
Score_{final} = min(\frac{Score_{raw}}{1000}, 1.0) \cdot 1000
\]

动态评估优化：
1. 早期游戏 (d ≤ 2): 增加位置权重 (\beta *= 1.2)
2. 中期游戏 (2 < d ≤ 6): 增加吃子权重 (\delta *= 1.3)
3. 晚期游戏 (d > 6): 增加协同权重 (\epsilon *= 1.4)
 */
	// 综合评估移动的分数，融合历史启发和启发式评估
	public static float evaluate_move((Card, (int, int)) move, GameState state, Dictionary<(int, int, int), float> history_table) {
		var (card, (row, col)) = move;
		var score = 0.0f;
		//1. 历史启发表分数 (基础权重 0.1)
		var history_score = history_table.GetValueOrDefault((card.card_id, row, col), 0f);
		score += MathF.Min(history_score * 0.1f, 1000); // 限制历史分数的最大值	
		// 2. 卡牌星级和边数值评估 (权重 2.0)
		var star = card.star;
		if (card.card_id != -1)
			star = get_card_star_map().GetValueOrDefault(card.card_id, 0);
		int[] card_edges = [card.up, card.down, card.left, card.right];
		//高星级卡牌在角落的评估

		if (star == 3 && card_edges.Count(8) >= 2 && CORNER_SPAN.Contains((row, col)))
			score += 40.0f; // 三星双8角落
		if (star == 4 && card_edges.Count(9) >= 2)
			score += 30.0f; // 四星双9
		if (star == 5 && card_edges.Count(9) >= 3)
			score += 50.0f; // 五星三9
		//3. 位置评估 (权重 1.5)
		if (CORNER_SPAN.Contains((row, col)))
			score += 15.0f; // 角落位置
		else if ((row, col) == (1, 1))
			score += 10.0f; // 中心位置
		// 4. 吃子评估 (权重 3.0) - 支持新规则
		(int, int, string, string)[] directions = [
			(-1, 0, "up", "down"),
			(1, 0, "down", "up"),
			(0, -1, "left", "right"),
			(0, 1, "right", "left")
		];
		foreach (var (dr, dc, my_dir, opp_dir) in directions) {
			var nr = row + dr;
			var nc = col + dc;
			if (nr < 0 || nr >= 3 || nc < 0 || nc >= 3) continue;
			var opp_card = state.board.get_card(nr, nc);
			if (opp_card != null && opp_card.owner != card.owner) {
				//使用新的比较方法来支持逆转和王牌杀手规则
				var result = card.compare_values(my_dir, opp_card, opp_dir, state.rules);
				if (result == 1) // 我方获胜
				{
					var my_value = my_dir switch {
						"up" => card.up,
						"down" => card.down,
						"left" => card.left,
						"right" => card.right,
						_ => 0
					};
					var opp_value = opp_dir switch {
						"up" => opp_card.up,
						"down" => opp_card.down,
						"left" => opp_card.left,
						"right" => opp_card.right,
						_ => 0
					};
					var diff = Math.Abs(my_value - opp_value);
					score += 30.0f + diff * 5.0f; // 基础吃子分30，每点数值差加5

					//如果是高星级卡吃低星级卡，额外加分
					if (star != null && opp_card.card_id != -1) {
						var opp_star = get_card_star_map().GetValueOrDefault(opp_card.card_id, 0);
						if (star > opp_star)
							score += (star.Value - opp_star) * 10.0f;
					}
				}
			}
		}
		//5. 边缘保护评估 (权重 1.0)
		//检查是否有己方卡牌在相邻位置
		(int, int)[] readonlySpan = [(-1, 0), (1, 0), (0, -1), (0, 1)];
		foreach (var (dr, dc) in readonlySpan) {
			var nr = row + dr;
			var nc = col + dc;
			if (nr is >= 0 and < 3 && nc is >= 0 and < 3) {
				var adj_card = state.board.get_card(nr, nc);
				if (adj_card != null && adj_card.owner == card.owner)
					score += 10.0f; // 相邻己方卡牌
			}
		}
		//6. 角落/高边战略评分 (新增)
		try {
			var handler = UnknownCardHandler.get_unknown_card_handler();
			if (handler != null) {
				var corner_score = UnknownCardHandler._calculate_corner_strategy_score(card, state.board);
				score += corner_score * CORNER_STRATEGY_WEIGHT;
			}
		} catch {
			//若处理器未初始化或计算失败，忽略该评分
		}
		// 7. 残局暴露面惩罚
		score -= _calculate_endgame_exposure_penalty(card, row, col, state);

		return MathF.Min(score, 1000f); // 确保最终分数不会过大
	}

	//使用综合评估函数对移动进行排序
	public static List<(Card, (int, int))> order_moves(List<(Card, (int, int))> moves, GameState state, Dictionary<(int, int, int), float> history_table) {
		return moves.OrderBy(move => -evaluate_move(move, state, history_table)).ToList();
	}

	public static string get_state_hash(GameState state) {
		//生成状态的哈希值
		var board_str = state.board.ToString();
		var hands_str = new StringBuilder();
		foreach (var player in state.players)
		foreach (var card in player.hand)
			hands_str.Append(card.card_id);
		return $"{board_str}_{hands_str}_{state.current_player_idx}";
	}

	//检查是否超时
	public static bool is_time_up() => (DateTime.Now - START_TIME).TotalSeconds > TIME_LIMIT;

	//改进的极小极大搜索，使用make_move/undo_move机制避免深拷贝
	public static SearchResult minimax(GameState state, int depth, float alpha, float beta, bool maximizing, int ai_player_idx, bool verbose = false, bool is_root = false, Dictionary<(int, int, int), float>?
		history_table = null, List<(Card, (int, int))>? path = null, Action<ProgressInfo>? progress_callback = null) {
		// global SEARCH_STATS, PROGRESS_LAST_TIME
		SEARCH_STATS.nodes_searched += 1;

		if (progress_callback != null) {
			var now = DateTime.Now;
			if ((now - PROGRESS_LAST_TIME).TotalSeconds >= PROGRESS_INTERVAL) {
				PROGRESS_LAST_TIME = now;
				progress_callback(new() {
					phase = "searching",
					depth = SEARCH_STATS.depth_completed + 1,
					max_depth = depth,
					best_move = null,
					best_score = 0,
					nodes_searched = SEARCH_STATS.nodes_searched,
					time_elapsed = (float)(DateTime.Now - START_TIME).TotalSeconds,
					time_remaining = TIME_LIMIT - (float)(DateTime.Now - START_TIME).TotalSeconds,
					stats = SEARCH_STATS.get_summary()
				});
			}
		}
		if (path == null)
			path = [];
		if (history_table == null)
			history_table = [];
		//检查时间限制
		if (is_time_up())
			return new SearchResult(evaluate_state(state, ai_player_idx), null, path);
		//检查终止条件
		if (depth == 0 || state.is_game_over())
			return new SearchResult(evaluate_state(state, ai_player_idx), null, path);
		// 置换表查找
		var state_hash = get_state_hash(state);
		if (TRANSPOSITION_TABLE.ContainsKey(state_hash)) {
			SEARCH_STATS.tt_hits += 1;
			var tt_entry = TRANSPOSITION_TABLE[state_hash];
			if (tt_entry.depth >= depth) {
				SEARCH_STATS.tt_cutoffs += 1;
				return new SearchResult(tt_entry.score, tt_entry.move, path);
			}
		}
		var current_player = state.players[state.current_player_idx];
		var playable_cards = current_player.get_playable_cards(state.rules);
		var moves = playable_cards.SelectMany(card => state.board.available_positions().Select(pos => (card, pos))).ToList();
		if (moves.Count() == 0)
			return new SearchResult(evaluate_state(state, ai_player_idx), null, path);
		// 移动排序
		moves = order_moves(moves, state, history_table);
		(Card card, (int, int) pos)? best_move = null;
		List<(Card, (int, int))> best_path = [];
		var moves_evaluated = 0;
		if (maximizing) {
			var max_eval = float.MinValue;
			foreach (var move in moves) {
				if (is_time_up()) break;
				moves_evaluated += 1;
				SEARCH_STATS.move_evaluations += 1;
				var (card, (row, col)) = move;
				//使用make_move代替深拷贝
				var move_record = state.make_move(row, col, card);
				if (move_record == null) continue; // 无效移动
				try {
					var result = minimax(state, depth - 1, alpha, beta, false, ai_player_idx,
						verbose, false, history_table, path.Concat([move]).ToList(), progress_callback);

					if (result.eval_score > max_eval) {
						max_eval = result.eval_score;
						best_move = move;
						best_path = result.path;
					}

					alpha = MathF.Max(alpha, result.eval_score);
				} finally {
					//始终撤销移动
					state.undo_move(move_record);
				}
				if (beta <= alpha) {
					SEARCH_STATS.alpha_beta_cutoffs += 1;
					// 更新历史启发表
					if (best_move != null) {
						(card, (row, col)) = best_move.Value;
						var key = (card.card_id, row, col);
						history_table[key] = MathF.Min(history_table.GetValueOrDefault(key, 0) + MathF.Pow(2, depth), 1000000);
					}
					break;
				}
			}
			//更新置换表
			TRANSPOSITION_TABLE[state_hash] = new() {
				depth = depth,
				score = max_eval,
				move = best_move
			};
			//计算分支因子
			var branching_factor = depth > 1 ? moves_evaluated : moves.Count();
			return new SearchResult(max_eval, best_move, best_path, new() { branching_factor = branching_factor });
		} else {
			var min_eval = float.MaxValue;
			foreach (var move in moves) {
				if (is_time_up()) break;
				moves_evaluated += 1;
				SEARCH_STATS.move_evaluations += 1;
				var (card, (row, col)) = move;
				//使用make_move代替深拷贝
				var move_record = state.make_move(row, col, card);
				if (move_record == null) continue; // 无效移动
				try {
					var result = minimax(state, depth - 1, alpha, beta, true, ai_player_idx,
						verbose, false, history_table, path.Concat([move]).ToList(), progress_callback);

					if (result.eval_score < min_eval) {
						min_eval = result.eval_score;
						best_move = move;
						best_path = result.path;
					}

					alpha = MathF.Min(alpha, result.eval_score);
				} finally {
					//始终撤销移动
					state.undo_move(move_record);
				}
				if (beta <= alpha) {
					SEARCH_STATS.alpha_beta_cutoffs += 1;
					// 更新历史启发表
					if (best_move != null) {
						(card, (row, col)) = best_move.Value;
						var key = (card.card_id, row, col);
						history_table[key] = MathF.Min(history_table.GetValueOrDefault(key, 0) + MathF.Pow(2, depth), 1000000);
					}
					break;
				}
			}
			//更新置换表
			TRANSPOSITION_TABLE[state_hash] = new() {
				depth = depth,
				score = min_eval,
				move = best_move
			};
			//计算分支因子
			var branching_factor = depth > 1 ? moves_evaluated : moves.Count();
			return new SearchResult(min_eval, best_move, best_path, new() { branching_factor = branching_factor });
		}
	}

	//增强的迭代加深搜索，包含详细进度显示
	public static ((Card card, (int, int) pos)? best_move, List<(Card, (int, int))> best_path) iterative_deepening_search(GameState state, float max_time, bool verbose = false, int max_depth = 100, Action<ProgressInfo>? progress_callback = null) {
		START_TIME = DateTime.Now;
		TIME_LIMIT = max_time;
		PROGRESS_LAST_TIME = DateTime.MinValue;
		//重置搜索统计
		SEARCH_STATS.reset();
		Dictionary<(int, int, int), float> history_table = [];
		(Card card, (int, int) pos)? best_move = null;
		List<(Card, (int, int))> best_path = [];
		var depth = 1;
		if (verbose) {
			for (var i = 0; i < 60; i++) print("=");
			println();
			println("开始迭代加深搜索");
			println($"时间限制: {max_time}秒, 最大深度: {max_depth}");
			for (var i = 0; i < 60; i++) print("=");
			println();
		}
		while (!is_time_up() && depth <= max_depth) {
			var depth_start_time = DateTime.Now;
			var nodes_before = SEARCH_STATS.nodes_searched;
			if (verbose) {
				print("\n");
				for (var i = 0; i < 20; i++) print("=");
				println();
				println($" 深度 {depth} ");
				for (var i = 0; i < 20; i++) print("=");
				println();
				println($"开始时间: {depth_start_time}");
				var remaining_time = TIME_LIMIT - (depth_start_time - START_TIME).TotalSeconds;
				println($"剩余时间: {remaining_time:.2f}秒");
			}
			var result = minimax(state, depth, float.MinValue, float.MaxValue, true,
				state.current_player_idx, verbose, true, history_table, progress_callback: progress_callback);
			var depth_end_time = DateTime.Now;
			var depth_time = (float)(depth_end_time - depth_start_time).TotalSeconds;
			var nodes_this_depth = SEARCH_STATS.nodes_searched - nodes_before;
			//检查是否超时
			if (is_time_up() && depth > 1) {
				if (verbose)
					println($"深度 {depth} 搜索超时，使用深度 {depth - 1} 的结果");
				break;
			}
			best_move = result.best_move;
			best_path = result.path;
			//计算分支因子
			var branching_factor = result.stats.branching_factor;
			//记录深度统计
			SEARCH_STATS.add_depth_stats(depth, nodes_this_depth, depth_time, result.eval_score, best_move, branching_factor);
			if (verbose) {
				println($"深度 {depth} 完成:");
				println($"  最佳移动: {format_move_display(best_move)}");
				println($"  评分: {result.eval_score:.3f}");
				println($"  搜索节点: {nodes_this_depth:,}");
				println($"  用时: {depth_time:.3f}秒");
				println($"  节点/秒: {nodes_this_depth / depth_time:,.0f}");
				println($"  分支因子: {branching_factor:.2f}");

				// 显示搜索统计
				var tt_hit_rate = SEARCH_STATS.tt_hits / Math.Max(SEARCH_STATS.nodes_searched, 1) * 100;
				var cutoff_rate = SEARCH_STATS.alpha_beta_cutoffs / Math.Max(SEARCH_STATS.nodes_searched, 1) * 100;
				println($"  置换表命中率: {tt_hit_rate:.1f}%");
				println($"  α-β剪枝率: {cutoff_rate:.1f}%");

				//显示评分趋势
				if (SEARCH_STATS.best_score_history.Count >= 2) {
					var score_change = SEARCH_STATS.best_score_history[^1] - SEARCH_STATS.best_score_history[^2];
					var trend = score_change > 0 ? "↑" : score_change < 0 ? "↓" : "→";
					println($"  评分变化: {score_change:+.3f} {trend}");
				}
			}
			//回调函数更新
			if (progress_callback != null) {
				progress_callback(new() {
					depth = depth,
					max_depth = max_depth,
					best_move = best_move,
					best_score = result.eval_score,
					nodes_searched = SEARCH_STATS.nodes_searched,
					time_elapsed = (float)(DateTime.Now - START_TIME).TotalSeconds,
					time_remaining = TIME_LIMIT - (float)(DateTime.Now - START_TIME).TotalSeconds,
					stats = SEARCH_STATS.get_summary()
				});
			}
			depth += 1;
			//自适应深度限制
			if (depth > max_depth) {
				if (verbose)
					println($"达到最大深度限制({max_depth})，结束搜索");
				break;
			}
			//预测下一深度所需时间（更宽松的判断）
			if (depth_time > 0 && depth >= 3) {
				//至少完成3层搜索再考虑时间限制
				// 考虑置换表命中率的影响，命中率高时搜索会更快
				var tt_hit_rate = SEARCH_STATS.tt_hits / Math.Max(SEARCH_STATS.nodes_searched, 1);
				var tt_speedup_factor = 1.0f - MathF.Min(tt_hit_rate * 0.3f, 0.4f); // 最多40%的加速
				// 更保守的分支因子估算，考虑剪枝效果
				var effective_branching = branching_factor > 1 ? Math.Max(branching_factor * 0.8f, 2.0f) : 2.5f;
				//估算时间，考虑各种优化因素
				var base_estimation = depth_time * Math.Pow(effective_branching, 1.2f); // 降低指数从1.5到1.2
				var estimated_next_time = base_estimation * tt_speedup_factor;
				var remaining_time = TIME_LIMIT - (DateTime.Now - START_TIME).TotalSeconds;
				//更宽松的缓冲时间：使用95%的剩余时间，且有最小时间保证
				var time_threshold = Math.Max(remaining_time * 0.95f, 0.1f); // 预留5%缓冲时间，或至少0.1秒
				if (estimated_next_time > time_threshold && verbose) {
					println($"预计深度 {depth} 需要 {estimated_next_time:.2f}秒，超过可用时间 {time_threshold:.2f}秒");
					println($"  (分支因子: {branching_factor:.2f} → 有效: {effective_branching:.2f}, 置换表加速: {(1 - tt_speedup_factor) * 100:.1f}%)");
				}
			}
		}
		if (verbose) {
			println();
			for (var i = 0; i < 60; i++) print("=");
			println();
			println("搜索完成总结:");
			var summary = SEARCH_STATS.get_summary();
			println($"  最终深度: {SEARCH_STATS.depth_completed}");
			println($"  总搜索节点: {summary.total_nodes:,}");
			println($"  总用时: {summary.total_time:.3f}秒");
			println($"  平均节点/秒: {summary.nodes_per_second:,.0f}");
			println($"  置换表命中率: {summary.tt_hit_rate * 100:.1f}%");
			println($"  α-β剪枝率: {summary.cutoff_rate * 100:.1f}%");
			println($"  平均分支因子: {summary.avg_branching_factor:.2f}");
			println($"  最终最佳移动: {format_move_display(best_move)}");
			println();
			for (var i = 0; i < 60; i++) print("=");
			println();
		}

		return (best_move, best_path);
	}

	//格式化移动显示
	public static string format_move_display((Card card, (int, int) pos)? move) {
		if (move == null)
			return "无移动";

		var (card, (row, col)) = move.Value;
		var star_map = get_card_star_map();
		var star = star_map?.GetValueOrDefault(card.card_id, '?');
		return $"卡牌U{card.up}R{card.right}D{card.down}L{card.left}(★{star}) → 位置({row},{col})";
	}

	//并行搜索入口，增强进度显示
	public static ((Card, (int, int) )? best_move, List<(Card, (int, int))> best_path) find_best_move_parallel(GameState game_state, int max_depth = 9, bool verbose = false, float max_time = 5,
		object? all_cards = null, object? n_jobs = null, Action<ProgressInfo>? progress_callback = null, string open_mode = "none") {
		// 清理全局状态
		TRANSPOSITION_TABLE = [];

		// 使用迭代加深搜索，限制最大深度为100
		var (best_move, best_path) = iterative_deepening_search(
			game_state,
			max_time, // 5秒时间限制
			verbose,
			Math.Min(max_depth, 100), // 确保不超过100层
			progress_callback
		);

		return (best_move, best_path);
	}
}
using System;
using System.Collections.Generic;
using System.Linq;
using TtcServer.core;
using static TtcServer.Utils;

namespace TtcServer.ai;

public class MonteCarlo {
	public class MonteCarloSolver {
		private GameState original_state;
		private List<Card> all_cards;
		private int ai_player_idx;
		private float time_limit;
		private DateTime start_time;
		//记录对手玩家的索引
		private int opp_idx;
		//构建卡牌ID到Card对象的快速查找表
		private Dictionary<int, Card> _card_by_id;
		//收集已确定使用的卡牌ID
		private HashSet<int> used_card_ids;
		//可用卡池（用于采样对手未知手牌）
		private Dictionary<int, int> card_star_map = [];
		private Dictionary<int, string> card_type_map = [];
		private List<KeyValuePair<int, int>> available_pool_ids;
		private int opp_unknown_count;
		private List<int> opp_known_indices = [];
		public int opp_hand_slots; //对手手牌槽位总数

		private MonteCarloSolver(GameState game_state, List<Card> all_cards, int ai_player_idx, float time_limit = 5.0f) {
			original_state = game_state;
			this.all_cards = all_cards;
			this.ai_player_idx = ai_player_idx;
			this.time_limit = time_limit;
			opp_idx = 1 - ai_player_idx;
			_card_by_id = all_cards.Where(c => c.card_id != -1).ToDictionary(c => c.card_id, c => c);
			used_card_ids = _collect_used_card_ids(game_state);
			foreach (var c in this.all_cards) {
				if (c.card_id != -1) {
					card_star_map[c.card_id] = c.star ?? 1;
					card_type_map[c.card_id] = c.card_type;
				}
			}
			available_pool_ids = card_star_map.Where(cid => !used_card_ids.Contains(cid.Key)).ToList();
			//统计对手未知手牌数量（同时检测真正的占位符和 AI 生成的预测卡牌）
			var opp_player = game_state.players[opp_idx];
			for (var i = 0; i < opp_player.hand.Count; i++) {
				var card = opp_player.hand[i];
				if (_is_unknown_or_generated(card))
					opp_unknown_count++;
				else opp_known_indices.Add(i);
				opp_hand_slots++;
			}
		}

		// 内部工具方法
		private static HashSet<int> _collect_used_card_ids(GameState state) {
			HashSet<int> used = [];
			for (var r = 0; r < 3; r++) {
				for (var c = 0; c < 3; c++) {
					var card = state.board.get_card(r, c);
					if (card != null && card.card_id != -1)
						used.Add(card.card_id);
				}
			}
			foreach (var player in state.players) {
				foreach (var card in player.hand) {
					if (card.card_id != -1 && !_is_unknown_or_generated(card))
						used.Add(card.card_id);
				}
			}
			return used;
		}

		//判断是否为未知卡牌（占位符 或 AI生成的预测卡）
		private static bool _is_unknown_or_generated(Card card) {
			//原始未知占位符（全0）
			if (card is { up: 0, right: 0, down: 0, left: 0 })
				return true;
			// AI 生成的预测卡牌（UnknownCardHandler 标记）
			if (card._is_generated || card._is_prediction)
				return true;
			//card_id >= 1000 是预测卡牌的 ID 偏移
			if (card.card_id is >= 1000)
				return true;
			return false;
		}

		//从卡牌ID构建Card对象
		private Card? _build_card_from_id(int card_id, string? owner = null, bool can_use = true) {
			_card_by_id.TryGetValue(card_id, out var src);
			if (src == null)
				// fallback: 从all_cards重建
				return null;
			return new Card(
				src.base_up,
				src.base_right,
				src.base_down,
				src.base_left,
				owner) {
				card_id = card_id,
				card_type = src.card_type,
				can_use = can_use
			};
		}

		//对手手牌采样
		/*
		 *    为对手的未知手牌采样具体卡牌
        返回值：完整的对手手牌列表（包括已知 + 采样）
		 */
		private List<Card?> _sample_opponent_hand_cards() {
			var opp_player = original_state.players[opp_idx];
			//采样未知卡牌ID
			var pool = available_pool_ids.Select(cid => cid.Key).Shuffle().ToList();
			//构建完整手牌
			List<Card?> result = [];
			using var sampled_iter = pool.Take(opp_unknown_count).GetEnumerator();
			for (var i = 0; i < opp_player.hand.Count; i++) {
				var card = opp_player.hand[i];
				if (opp_known_indices.Contains(i))
					result.Add(_build_card_from_id(card.card_id, card.owner, card.can_use));
				else {
					var cid = -1;
					try {
						cid = sampled_iter.Current;
						sampled_iter.MoveNext();
					} catch {
						cid = pool.Count > 0 ? pool[Random.Shared.Next(pool.Count)] : 1;
					}
					var new_card = _build_card_from_id(
						cid, card.owner, card.can_use);
					if (new_card != null)
						result.Add(new_card);
					else
						result.Add(card.Clone() as Card); // fallback
				}
			}
			return result;
		}

		//构建模拟用的GameState
		/*
		 *    构建一次模拟使用的GameState：
        - 复制棋盘
        - 己方手牌不变
        - 对手未知手牌重新采样
		 */
		private GameState _build_simulation_state() {
			//复制棋盘
			var board_copy = (Board)original_state.board.Clone();
			//己方手牌（深拷贝）
			var my_player = original_state.players[ai_player_idx];
			var my_hand_copy = my_player.hand.Select(c => c.Clone()).Cast<Card>().ToList();
			//对手手牌（采样未知卡牌）
			var opp_hand_copy = _sample_opponent_hand_cards();
			//正确的玩家顺序
			Player[] players = ai_player_idx == 0
				? [
					new Player(my_player.name, my_hand_copy),
					new Player(original_state.players[1].name, opp_hand_copy)
				]
				: [
					new Player(original_state.players[0].name, opp_hand_copy),
					new Player(my_player.name, my_hand_copy)
				];
			var sim_state = new GameState(
				board_copy, players,
				original_state.current_player_idx,
				[..original_state.rules]
			);
			//应用同类规则
			if (sim_state.rules.Contains("同类强化") || sim_state.rules.Contains("同类弱化"))
				sim_state.recalculate_type_modifiers();
			return sim_state;
		}

		//随机模拟到终局	
		private float _random_playout(GameState state) {
			const int max_moves = 10; // 安全上限，防止死循环
			var moves_done = 0;
			while (!state.is_game_over() && moves_done < max_moves) {
				moves_done++;
				var player = state.players[state.current_player_idx];
				var playable = player.get_playable_cards(state.rules);
				if (playable.Count == 0)
					break;
				var card = playable[Random.Shared.Next(playable.Count)];
				var available = state.board.available_positions();
				if (available.Count == 0)
					break;
				var pos = available[Random.Shared.Next(available.Count)];
				try {
					state.make_move(pos.Item1, pos.Item2, card);
				} catch {
					break;
				}
			}
			var winner = state.get_winner();
			if (winner is null) return 0.0f;
			var ai_name = original_state.players[ai_player_idx].name;
			return winner == ai_name ? 1.0f : -1.0f;
		}

		//评估单个走法
		public float evaluate_move(Card card, int row, int col, int num_simulations) {
			var total = 0.0f;
			var valid = 0;
			for (var i = 0; i < num_simulations; i++) {
				var sim_state = _build_simulation_state();
				//当前模拟状态中执行目标走法
				//注意：sim_state中的card引用可能不是同一个对象，需要找到对应的
				var my_player_sim = sim_state.players[sim_state.current_player_idx];
				var my_card = my_player_sim.hand.FirstOrDefault(c => c.card_id == card.card_id);
				if (my_card is null) {
					//fallback：使用手牌中的同名卡
					var playable = my_player_sim.get_playable_cards(sim_state.rules);
					if (playable.Count > 0)
						my_card = playable[0];
					else
						continue;
				}
				try {
					sim_state.make_move(row, col, my_card);
				} catch {
					continue;
				}
				total += _random_playout(sim_state);
				valid++;
			}
			return total / Math.Max(valid, 1);
		}

		//主入口：寻找最佳走法
		/*
		 *   寻找当前局面下的最佳走法。

        Parameters
        ----------
        base_simulations : int
            每个候选走法的基准模拟次数

        Returns
        -------
        (best_move, move_scores)
            best_move  : (card, (row, col)) 或 None
            move_scores: [(move, score), ...] 所有评估过的走法及其分数
		 */
		public ((Card card, (int row, int col))?, List<((Card card, (int row, int col)), float)>) find_best_move(int base_simulations = 150) {
			start_time = DateTime.Now;
			var moves = original_state.get_available_moves();
			if (moves.Count == 0) return (null, []);
			//如果对手没有未知卡牌，可以用更少的模拟
			if (opp_unknown_count == 0)
				base_simulations = Math.Max(50, base_simulations / 3);
			List<((Card card, (int row, int col)), float)> move_scores = [];
			moves = moves.Shuffle().ToList();
			for (var idx = 0; idx < moves.Count; idx++) {
				var (card, (row, col)) = moves[idx];
				var elapsed = DateTime.Now - start_time;
				if (elapsed.TotalSeconds > time_limit) break;
				//动态调整剩余走法的模拟次数
				var remaining = moves.Count - idx;
				var sims = base_simulations;
				if (remaining > 0) {
					var time_per_move = (time_limit - elapsed.TotalSeconds) / remaining;
					//粗略估计：每次模拟约0.002-0.01秒
					sims = Math.Min(base_simulations, Math.Max(30, (int)(time_per_move / 0.005f)));
				}
				var score = evaluate_move(card, row, col, sims);
				move_scores.Add(((card, (row, col)), score));
			}
			if (move_scores.Count == 0)
				return (null, []);
			//选择最高分的走法
			var best_move = move_scores.OrderByDescending(x => x.Item2).First().Item1;
			return (best_move, move_scores);
		}

//便捷函数 - 与现有 ai_server.py 接口兼容
/*
 * 使用蒙特卡洛方法找到最佳走法。
    接口与 find_best_move_parallel 兼容。

    Parameters
    ----------
    game_state : GameState
        当前游戏状态
    all_cards : List[Card]
        完整卡牌数据库
    my_owner : str
        己方标识 ('red' / 'blue')
    time_limit : float
        时间预算（秒）
    base_simulations : int
        每个走法的基准模拟次数
    verbose : bool
        是否输出详细信息

    Returns
    -------
    (best_move, best_path)
        best_move : (card, (row, col)) 或 None
        best_path : []  占位，与 minimax 接口兼容
 */
		public static ((Card card, (int row, int col))?, List<((Card card, (int row, int col)), float)>)
			monte_carlo_best_move(GameState game_state, List<Card> all_cards, string my_owner, float time_limit = 5.0f, int base_simulations = 150, bool verbose = false) {
			var ai_player_idx = game_state.players[0].name == "me" ? 0 : 1;
			var solver = new MonteCarloSolver(
				game_state,
				all_cards,
				ai_player_idx,
				time_limit
			);
			if (verbose) {
				println($"[MC] 对手未知手牌: {solver.opp_unknown_count} 张");
				println($"[MC] 可用卡池: {solver.available_pool_ids.Count} 张");
				println($"[MC] 基准模拟次数: {base_simulations}");
			}
			var (best_move, move_scores) = solver.find_best_move(
				base_simulations = base_simulations
			);
			if (verbose && move_scores.Count > 0) {
				println($"[MC] 评估了 {move_scores.Count} 个走法:");
				foreach (var ((card, (row, col)), score) in move_scores.OrderByDescending(x => x.Item2).Take(5)) {
					println($"    卡牌ID={card.card_id} ({card.display()}) at ({row},{col}) → 胜率期望={score:+.3f}");
				}
			}
			return (best_move, []);
		}

		//卡组构建 - 参考 LinkRoss Sample/SolverA 的 get_deck 逻辑
		/*
		 *  使用蒙特卡洛采样构建最优卡组。
    参考 LinkRoss SolverA.get_deck() 的规则感知卡组选择策略。

    Parameters
    ----------
    available_cards : List[Card]
        可用的卡牌列表
    rules : List[str]
        当前规则列表
    card_event_cards : List[Card]
        对手可能的卡牌（如果已知NPC卡组）
    card_type_map : Dict[int, str]
        卡牌ID → 卡牌类型
    card_star_map : Dict[int, int]
        卡牌ID → 星级

    Returns
    -------
    List[int]
        5张卡牌的ID列表（最优卡组）
		 */
		public static List<int> build_deck_monte_carlo(List<Card> available_cards, List<string> rules, List<Card> card_event_cards, Dictionary<int, string>? card_type_map, Dictionary<int, int>? card_star_map) {
			if (card_star_map == null) card_star_map = [];
			Dictionary<int, List<int>> cards_by_star = new() {
				{ 1, [] },
				{ 2, [] },
				{ 3, [] },
				{ 4, [] },
				{ 5, [] }
			};
			Dictionary<string, Dictionary<int, List<int>>> cards_by_type = [];
			foreach (var card in available_cards) {
				var star = card_star_map.GetValueOrDefault(card.card_id, 3);
				cards_by_star[star].Add(card.card_id);
				if (card_type_map != null) {
					if (card_type_map.TryGetValue(card.card_id, out var ct)) {
						cards_by_type.TryAdd(ct, new Dictionary<int, List<int>> {
							{ 1, [] },
							{ 2, [] },
							{ 3, [] },
							{ 4, [] },
							{ 5, [] }
						});
						cards_by_type[ct][star].Add(card.card_id);
					}
				}
			}
			//规则感知选择策略
			var same = rules.Contains("同数");
			var plus = rules.Contains("加算");
			var rev = rules.Contains("逆转");
			var ace = rules.Contains("王牌杀手");
			var strengthen = rules.Contains("同类强化");
			var weaken = rules.Contains("同类弱化");
			//同类强化+非逆转 → 需要同类型
			//同类弱化+逆转 → 需要同类型
			var need_type = strengthen && !rev || weaken && rev;
			//同类弱化+非逆转 → 需要无类型
			//同类强化+逆转 → 需要无类型
			var need_no_type = weaken && !rev || strengthen && rev;
			List<int> choose = [];
			var cnt5 = 0;
			var cnt4 = 0;

			List<int> sample_from_star_pool(List<int> pool, int n) {
				//从指定星级池中随机采样n张
				var available = pool.Where(cid => !choose.Contains(cid)).ToList();
				return available.Sample(Math.Min(n, available.Count));
			}

			//Phase 1: 规则驱动的类型选择
			if (need_no_type && cards_by_type.Count > 0 && cards_by_type.Keys.Contains("")) {
				var no_type_pool = cards_by_type[""];
				if (no_type_pool.ContainsKey(5)) {
					cnt5 = 1;
					choose.AddRange(sample_from_star_pool(no_type_pool[5], 1));
				}
				if (no_type_pool.ContainsKey(4) && cnt4 + cnt5 < 2) {
					var n = Math.Min(2 - cnt4 - cnt5, no_type_pool[4].Count);
					cnt4 += n;
					choose.AddRange(sample_from_star_pool(no_type_pool[4], n));
				}
				for (var star = 3; star > 0; star--) {
					if (choose.Count >= 3) break;
					if (no_type_pool.ContainsKey(star)) {
						choose.AddRange(sample_from_star_pool(no_type_pool[star], 3 - choose.Count));
					}
				}
			} else if (need_type && card_event_cards.Count > 0 && cards_by_type.Count > 0) {
				//分析对手类型，选择对手没有的类型
				var enemy_types = new HashSet<string>();
				foreach (var c in card_event_cards) {
					if (card_type_map != null && card_type_map.TryGetValue(c.card_id, out var ct)) {
						enemy_types.Add(ct);
					}
				}
				//优先使用对手没有的类型
				var order = cards_by_type.Keys.Where(t => !string.IsNullOrEmpty(t) && !enemy_types.Contains(t)).ToList();
				order.AddRange(enemy_types);
				List<int> best_choose = [];
				foreach (var t in order) {
					var pool = cards_by_type[t];
					List<int> temp_choose = [];
					if (pool.TryGetValue(5, out var value))
						temp_choose.AddRange(sample_from_star_pool(value, 1));
					if (pool.TryGetValue(4, out var value2))
						temp_choose.AddRange(sample_from_star_pool(value2, Math.Min(2 - temp_choose.Count, value2.Count)));
					for (var star = 3; star > 0; star--) {
						if (temp_choose.Count >= 3) break;
						if (pool.TryGetValue(star, out var value3)) {
							temp_choose.AddRange(sample_from_star_pool(value3, 3 - temp_choose.Count));
						}
					}
					if (temp_choose.Count > best_choose.Count)
						best_choose = temp_choose;
				}
				choose = best_choose;
			}
			//Phase 2: 补全卡组到5张
			if (choose.Count < 5) {
				//优先5星
				if (cards_by_star[5].Count > 0 && cnt5 == 0) {
					cnt5 = 1;
					choose.AddRange(sample_from_star_pool(cards_by_star[5], 1));
				}
				//再补4星
				if (cards_by_star[4].Count > 0 && cnt4 + cnt5 < 2) {
					var n = Math.Min(2 - cnt4 - cnt5, cards_by_star[4].Count);
					cnt4 += n;
					choose.AddRange(sample_from_star_pool(cards_by_star[4], n));
				}
				//剩余用3-1星补全
				for (var star = 3; star > 0; star--) {
					if (choose.Count >= 5) break;
					if (cards_by_star[star].Count > 0) {
						choose.AddRange(sample_from_star_pool(cards_by_star[star], 5 - choose.Count));
					}
				}
			}
			//去重并补全到5张
			choose = new HashSet<int>(choose).ToList();
			if (choose.Count < 5) {
				var all_remaining = available_cards.Select(c => c.card_id).Where(cid => !choose.Contains(cid)).Shuffle().ToList();
				choose.AddRange(all_remaining.Take(5 - choose.Count));
			}
			return choose.Shuffle().Take(5).ToList();
		}
	}
}
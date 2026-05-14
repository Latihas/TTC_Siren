                                                                              using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Lumina.Excel.Sheets;
using TtcServer.core;
using static TtcServer.ai.AI;
using static TtcServer.Utils;

namespace TtcServer;

public partial class AiServer {
	[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")] 
	[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
	public class BoardJsonItem {
		public int[] pos { get; set; }
		public int numU { get; set; }
		public int numR { get; set; }
		public int numD { get; set; }
		public int numL { get; set; }
		public int owner { get; set; }
	}
	[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
	[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
	public class HandJsonItem {
		public int numU { get; set; }
		public int numR { get; set; }
		public int numD { get; set; }
		public int numL { get; set; }
		public bool canUse { get; set; } = true;
	}
	[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
	[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
	public class PostJson {
		public List<BoardJsonItem>? board { get; set; }
		public List<HandJsonItem>? myHand { get; set; }
		public List<HandJsonItem>? oppHand { get; set; }
		public int? myOwner { get; set; }
		public int? currentPlayer { get; set; }
		public string? rules { get; set; }
		public string? solver { get; set; }
		//default
		public bool show_search_progress { get; set; } = false;
		public int mc_simulations { get; set; } = 150;
	}

	//全局缓存和唯一ID查找表
	private static List<CardDatabaseEntry>? _card_db;
	private static List<Card>? _all_cards;
	private static Dictionary<(int, int, int, int), int>? _card_lookup;
	private static Lock _card_lock = new();
	private static Dictionary<int, int>? _card_star_map;
	private static Dictionary<int, string?>? _card_type_map;
	private static bool _handler_initialized;

	public static List<CardDatabaseEntry> get_card_db() {
		lock (_card_lock) {
			if (_card_db == null) {
				// using var reader = new StreamReader("data/幻卡数据库.csv");
				// using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
				// _card_db = csv.GetRecords<CardDatabaseEntry>().ToList();
				_card_db = DataManager.GameData.GetExcelSheet<TripleTriadCardResident>()!
					.Where(c => c is not { Top: 0, Bottom: 0, Left: 0, Right: 0 })
					.Select(c => new CardDatabaseEntry {
						Id = (int)c.RowId,
						Top = c.Top,
						Right = c.Right,
						Bottom = c.Bottom,
						Left = c.Left,
						Star = c.TripleTriadCardRarity.Value.Stars,
						TripleTriadCardType = c.TripleTriadCardType.RowId switch {
							1 => "蛮神",
							2 => "拂晓",
							3 => "兽人",
							4 => "帝国",
							_ => null
						},
						SortKey = c.SortKey,
						Order = c.Order,
						UIPriority = c.UIPriority,
						AcquisitionType = c.AcquisitionType.RowId
					}).ToList();
			}
		}
		return _card_db;
	}

	public static List<Card> get_all_cards() {
		if (_all_cards == null) {
			_all_cards = get_card_db().Select(row => new Card(
				row.Top,
				row.Right,
				row.Bottom,
				row.Left,
				null) {
				card_id = row.Id,
				card_type = row.TripleTriadCardType
			}).ToList();
		}
		return _all_cards;
	}

	public static Dictionary<(int, int, int, int), int> get_card_lookup() {
		if (_card_lookup == null) {
			// 用四面数值唯一确定卡牌ID
			_card_lookup = [];
			foreach (var row in get_card_db())
				_card_lookup[((row.Top), (row.Right), (row.Bottom), (row.Left))] = row.Id;
		}
		return _card_lookup;
	}

	public static Dictionary<int, int> get_card_star_map() {
		if (_card_star_map == null) {
			_card_star_map = [];
			foreach (var row in get_card_db())
				_card_star_map[row.Id] = row.Star;
		}
		return _card_star_map;
	}

	public static Dictionary<int, string?> get_card_type_map() {
		if (_card_type_map == null) {
			_card_type_map = [];
			foreach (var row in get_card_db()) {
				var card_id = row.Id;
				_card_type_map[card_id] = row.TripleTriadCardType;
			}
		}
		return _card_type_map;
	}

	//确保未知卡牌处理器已初始化
	internal  static void ensure_handler_initialized() {
		if (!_handler_initialized) {
			var all_cards = get_all_cards();
			var card_type_map = get_card_type_map();
			var card_star_map = get_card_star_map();
			UnknownCardHandler.initialize_unknown_card_handler(all_cards, card_type_map, card_star_map);
			_handler_initialized = true;
		}
	}

	public static string parse_owner(int owner) =>
		// 1=蓝方, 2=红方
		owner == 1 ? "blue" : "red";

	public static int? find_card_id_by_stats(int up, int right, int down, int left) {
		var lookup = get_card_lookup();
		if (lookup.TryGetValue((up, right, down, left), out var v)) return v;
		return null;
	}


	public static Board parse_board(List<BoardJsonItem> board_json) {
		var board = new Board();
		var type_map = get_card_type_map();
		foreach (var item in board_json) {
			var (r, c) = (item.pos[0], item.pos[1]);
			var (up, right, down, left) = (item.numU, item.numR, item.numD, item.numL);
			var card_id = find_card_id_by_stats(up, right, down, left);
			if (card_id is null) throw new Exception($"Board card not found in database: U{up} R{right} D{down} L{left}");
			var card_type = type_map[card_id.Value];
			var card = new Card(
				up,
				right,
				down,
				left,
				parse_owner(item.owner)) {
				card_id = card_id.Value,
				card_type = card_type
			};
			board.place_card(r, c, card);
		}
		return board;
	}

	//返回棋盘已占用格数
	private static int _board_occupancy(Board? board_state) {
		if (board_state == null) return 0;
		var occupied = 0;
		for (var r = 0; r < 3; r++)
		for (var c = 0; c < 3; c++)
			if (board_state.get_card(r, c) != null)
				occupied++;
		return occupied;
	}

	//评估未知候选牌在当前棋盘上的最大即时威胁
	private static float _score_unknown_candidate(Card card, Board? board_state, List<string> rules, string owner) {
		if (board_state == null) return card.up + card.right + card.down + card.left;
		(int, int, string, string)[] directions = [
			(-1, 0, "up", "down"),
			(1, 0, "down", "up"),
			(0, -1, "left", "right"),
			(0, 1, "right", "left")
		];
		var best_score = 0.0f;
		for (var row = 0; row < 3; row++)
		for (var col = 0; col < 3; col++) {
			if (!board_state.is_empty(row, col)) continue;
			var score = 0.0f;
			List<(Card, bool)> same_matches = [];
			Dictionary<int, List<(Card, bool)>> plus_sums = [];
			foreach (var (dr, dc, my_dir, opp_dir) in directions) {
				var nr = row + dr;
				var nc = col + dc;
				if (nr < 0 || nr >= 3 || nc < 0 || nc >= 3) continue;
				var target = board_state.get_card(nr, nc);
				if (target == null) continue;
				var target_is_enemy = target.owner != owner;
				if (target_is_enemy && card.compare_values(my_dir, target, opp_dir, rules) == 1)
					score += 6.0f;
				var my_value = card.get_effective_value(my_dir, rules);
				var target_value = target.get_effective_value(opp_dir, rules);
				if (my_value == target_value)
					same_matches.Add((target, target_is_enemy));
				var plus_sum = my_value + target_value;
				plus_sums.TryAdd(plus_sum, []);
				plus_sums[plus_sum].Add((target, target_is_enemy));
			}
			if (rules.Contains("同数") && same_matches.Count >= 2 && same_matches.Any(is_enemy => is_enemy.Item2))
				score += 8.0f * same_matches.Where(is_enemy => is_enemy.Item2).ToList().Count;
			if (rules.Contains("加算")) {
				foreach (var matches in plus_sums.Values)
					if (matches.Count >= 2 && matches.Any(is_enemy => is_enemy.Item2))
						score += 8.0f * matches.Count(is_enemy => is_enemy.Item2);
			}
			best_score = Math.Max(best_score, score);
		}
		var value_pressure = int.Max(int.Max(card.up, card.right), int.Max(card.down, card.left)) + (card.up + card.right + card.down + card.left) / 40.0f;
		return best_score * 10.0f + value_pressure;
	}

	//从未知候选池中选择实际填入手牌槽位的卡牌
	private static List<Card> _select_unknown_cards_for_slots(List<Card> candidates, int slot_count, Board board_state, List<string>? rules, string owner, bool is_opponent) {
		if (candidates.Count <= slot_count)
			return candidates;
		var occupied = _board_occupancy(board_state);
		var should_risk_select = is_opponent && (occupied >= 4 || slot_count <= 2 && occupied >= 3);
		if (!should_risk_select)
			return candidates.Sample(slot_count);
		var ranked = candidates
			.OrderByDescending(card => _score_unknown_candidate(
				card, board_state, rules ?? [], owner))
			.ThenByDescending(card => card.card_id == -1 ? 0 : card.card_id)
			.ToList();
		var selected = ranked.Take(slot_count).ToList();
		println(
			$"Endgame risk selection: {candidates.Count} candidates → {slot_count} slots (occupied={occupied}/9)");
		return selected;
	}

	private static bool _is_generated_unknown_card(Card card) => card._is_generated ||
	                                                             card._is_prediction ||
	                                                             card.card_id == -1 ||
	                                                             card.card_id >= 1000;

	private static int _count_unknown_slots_from_hand(List<Card> hand_cards) => hand_cards.Count(_is_generated_unknown_card);

	private static List<int> _get_unknown_hand_indices(List<Card> hand_cards) {
		List<int> result = [];
		for (var i = 0; i < hand_cards.Count; i++) {
			var card = hand_cards[i];
			if (_is_generated_unknown_card(card)) result.Add(i);
		}
		return result;
	}

	//返回推测牌对应的真实卡牌 ID。
	private static int? _base_card_id(Card card) {
		var card_id = card.card_id;
		if (card_id == -1) return null;
		if (card_id >= 1000) return card_id - 1000;
		return card_id;
	}

	//收集棋盘上已出现的真实卡牌 ID。
	private static HashSet<int> _known_card_ids_on_board(Board? board_state) {
		HashSet<int> known_ids = [];
		if (board_state == null) return known_ids;
		for (var row = 0; row < 3; row++)
		for (var col = 0; col < 3; col++) {
			var card = board_state.get_card(row, col);
			var card_id = card != null ? _base_card_id(card) : null;
			if (card_id.HasValue) known_ids.Add(card_id.Value);
		}
		return known_ids;
	}

	//收集明牌手牌中的真实卡牌 ID
	private static HashSet<int> _known_card_ids_in_hands(List<Card> hand) {
		HashSet<int> known_ids = [];
		foreach (var value in hand) {
			if (_is_generated_unknown_card(value)) continue;
			var card_id = _base_card_id(value);
			if (card_id.HasValue) known_ids.Add(card_id.Value);
		}
		return known_ids;
	}

	//统计一组已知卡牌占用的高星配额
	private static (int, int) _high_star_usage(List<Card> cards, Dictionary<int, int> star_map) {
		var high_star_count = 0;
		var five_star_count = 0;
		foreach (var card in cards) {
			if (_is_generated_unknown_card(card)) continue;
			var x = _base_card_id(card);
			var star = x.HasValue ? star_map.GetValueOrDefault(x.Value, 1) : 1;
			if (star >= 4) high_star_count += 1;
			if (star == 5) five_star_count += 1;
		}
		return (high_star_count, five_star_count);
	}

	//检查未知牌组合是否满足对手卡组星级限制
	private static bool _is_legal_unknown_assignment(List<Card> assignment, List<Card> known_opp_cards, Dictionary<int, int> star_map) {
		var (high_star_count, five_star_count) = _high_star_usage(known_opp_cards, star_map);
		HashSet<int> seen_ids = [];
		foreach (var card in assignment) {
			var card_id = _base_card_id(card);
			if (card_id.HasValue && seen_ids.Contains(card_id.Value)) return false;
			seen_ids.Add(card_id!.Value);
			var star = star_map.GetValueOrDefault(card_id.Value, 1);
			if (star >= 4)
				high_star_count += 1;
			if (star == 5)
				five_star_count += 1;
		}
		return high_star_count <= 2 && five_star_count <= 1;
	}

	/*
	 * 枚举符合当前已知信息和卡组限制的对手未知牌候选。

    Args:
        base_state: 当前局面，用于读取双方手牌。
        opp_hand: 对手当前手牌。
        board_state: 当前棋盘。
        opp_owner: 对手颜色。

    Returns:
        按数据库顺序生成的合法未知候选卡牌列表。
	 */
	private static List<Card> _build_legal_unknown_candidates(GameState base_state, List<Card> opp_hand, Board board_state, string opp_owner) {
		var star_map = get_card_star_map();
		var type_map = get_card_type_map();
		var known_global_ids = _known_card_ids_on_board(board_state);
		foreach (var player in base_state.players)
		foreach (var c in _known_card_ids_in_hands(player.hand))
			known_global_ids.Add(c);
		var known_opp_cards = opp_hand.Where(card => !_is_generated_unknown_card(card)).ToList();
		List<Card> candidate_cards = [];
		foreach (var card in get_all_cards()) {
			var card_id = _base_card_id(card);
			if (card_id.HasValue && known_global_ids.Contains(card_id.Value) || card_id == null)
				continue;
			var candidate = new Card(
				card.base_up,
				card.base_right,
				card.base_down,
				card.base_left,
				opp_owner) {
				card_id = card_id.Value,
				card_type = type_map[card_id.Value],
				can_use = true
			};
			if (!_is_legal_unknown_assignment([candidate], known_opp_cards, star_map))
				continue;
			candidate._is_generated = true;
			candidate._is_prediction = true;
			candidate_cards.Add(candidate);
		}
		return candidate_cards;
	}

	//按卡牌身份去重，保留候选池中的首个副本
	private static List<Card> _unique_unknown_candidates(List<Card> candidates) {
		List<Card> unique = [];
		HashSet<(int, int, int, int, int, string?)> seen = [];
		foreach (var card in candidates) {
			var key = (
				card.card_id,
				card.base_up,
				card.base_right,
				card.base_down,
				card.base_left,
				card.card_type);
			if (!seen.Add(key)) continue;
			unique.Add(card);
		}
		return unique;
	}

	private static GameState _copy_state_with_unknown_assignment(GameState base_state, int opp_player_idx, List<int> unknown_indices, List<Card> sampled_cards) {
		var scenario = (GameState)base_state.Clone();
		var opp_hand_copy = scenario.players[opp_player_idx].hand;
		foreach (var (idx, sampled_card) in unknown_indices.Zip(sampled_cards)) {
			var copied_card = (Card)sampled_card.Clone();
			copied_card.owner = opp_hand_copy[idx].owner;
			copied_card.can_use = opp_hand_copy[idx].can_use;
			opp_hand_copy[idx] = copied_card;
		}
		return scenario;
	}

/*
 * 构建残局信息集场景，优先覆盖对手高威胁未知牌组合。

    Args:
        base_state: 当前搜索局面。
        opp_hand: 当前对手手牌，未知槽位已由预测牌占位。
        used_cards: 已知使用过的卡牌 ID。
        rules: 当前规则列表。
        board_state: 当前棋盘。
        opp_owner: 对手颜色。
        opp_player_idx: 对手在 GameState.players 中的索引。
        sample_count: 最多构建的可能世界数量。

    Returns:
        一组 GameState 副本；无未知牌时仅返回当前局面。
 */
	private static List<GameState> _build_endgame_scenarios(GameState base_state, List<Card> opp_hand, HashSet<int> used_cards, List<string> rules, Board board_state, string opp_owner, int opp_player_idx, int sample_count) {
		var handler = UnknownCardHandler.get_unknown_card_handler();
		var unknown_indices = _get_unknown_hand_indices(opp_hand);
		if (!unknown_indices.Any())
			return [base_state];
		if (handler == null)
			return [base_state];
		var known_opp_hand = opp_hand.Where(card => !_is_generated_unknown_card(card)).ToList();
		var legal_candidates = _build_legal_unknown_candidates(base_state, opp_hand, board_state, opp_owner);
		List<Card> candidates;
		if (legal_candidates.Count > 0) {
			candidates = legal_candidates;
			println(
				$"Endgame legal enumeration: {legal_candidates.Count} candidates for {unknown_indices.Count} unknown slots");
		} else
			candidates = handler.generate_opponent_cards(
				unknown_indices.Count,
				rules,
				[..used_cards],
				board_state,
				known_opp_hand,
				opp_owner,
				true);
		candidates = _unique_unknown_candidates(candidates);
		if (candidates.Count == 0)
			return [base_state];
		var ranked_candidates = candidates
			.OrderByDescending(card => _score_unknown_candidate(
				card,
				board_state,
				rules ?? [],
				opp_owner))
			.ThenByDescending(card => card.up + card.right + card.down + card.left)
			.ThenByDescending(card => -(card.card_id == -1 ? 0 : card.card_id))
			.ToList();
		var unknown_count = unknown_indices.Count;
		var candidate_limit = Math.Min(ranked_candidates.Count, Math.Max(sample_count * unknown_count, unknown_count));
		var scenario_limit = Math.Max(1, sample_count);
		List<GameState> scenarios = [];
		List<int[]> seen_assignments = [];
		var star_map = get_card_star_map();
		foreach (var assignment in ranked_candidates.Take(candidate_limit).GeneratePermutations(unknown_count)) {
			var assignment_key = assignment.Select(card => card.card_id).ToArray();
			if (seen_assignments.Contains(assignment_key))
				continue;
			if (!_is_legal_unknown_assignment(assignment, known_opp_hand, star_map))
				continue;
			seen_assignments.Add(assignment_key);
			scenarios.Add(_copy_state_with_unknown_assignment(
				base_state,
				opp_player_idx,
				unknown_indices,
				assignment
			));
			if (scenarios.Count >= scenario_limit)
				break;
		}
		return scenarios.Count > 0 ? scenarios : [base_state];
	}

	//返回终局或近终局时 AI 视角的牌数差。
	private static int _endgame_score(GameState state, int ai_player_idx) {
		var (red_count, blue_count) = state.count_cards();
		return ai_player_idx == 0 ? red_count - blue_count : blue_count - red_count;
	}

	//为残局完全搜索构建稳定缓存键
	private static (int current_player_idx, (int, string?, int, int, int, int, int)?[], (int, string?, bool, int, int, int, int, int)[][], string[]) _endgame_state_key(GameState state) {
		List<(int, string?, int, int, int, int, int)?> board_key = [];
		for (var r = 0; r < 3; r++) {
			for (var c = 0; c < 3; c++) {
				var card = state.board.get_card(r, c);
				if (card == null)
					board_key.Add(null);
				else
					board_key.Add((
						card.card_id,
						card.owner,
						card.type_modifier,
						card.base_up,
						card.base_right,
						card.base_down,
						card.base_left));
			}
		}
		List<(int, string?, bool, int, int, int, int, int)[]> hand_key = [];
		foreach (var player in state.players) {
			hand_key.Add(player.hand.Select(card => (
				card.card_id,
				card.owner,
				card.can_use,
				card.type_modifier,
				card.base_up,
				card.base_right,
				card.base_down,
				card.base_left)).ToArray());
		}
		return (state.current_player_idx, board_key.ToArray(), hand_key.ToArray(), state.rules.ToArray());
	}

	/*
	 * 对空位很少的残局做完整 Minimax 终局搜索。

    Args:
        state: 当前局面，会在搜索中原地落子并撤销。
        ai_player_idx: AI 玩家索引。
        cache: 本次信息集搜索的局面缓存。

    Returns:
        AI 视角终局牌数差；正数更好，负数更差。
	 */
	private static int _solve_endgame_exact(GameState state, int ai_player_idx, Dictionary<(int current_player_idx, (int, string?, int, int, int, int, int)?[], (int, string?, bool, int, int, int, int, int)[][], string[]), int>? cache = null) {
		if (cache == null)
			cache = [];
		if (state.is_game_over())
			return _endgame_score(state, ai_player_idx);

		var key = _endgame_state_key(state);
		if (cache.TryGetValue(key, out var v))
			return v;
		var moves = state.get_available_moves();
		if (moves.Count == 0) {
			var score = _endgame_score(state, ai_player_idx);
			cache[key] = score;
			return score;
		}
		var maximizing = state.current_player_idx == ai_player_idx;
		int best_score;
		if (maximizing) {
			best_score = int.MinValue;
			foreach (var (card, (row, col)) in moves) {
				var move_record = state.make_move(row, col, card);
				if (move_record == null)
					continue;
				try {
					best_score = Math.Max(best_score, _solve_endgame_exact(state, ai_player_idx, cache));
				} finally {
					state.undo_move(move_record);
				}
			}
		} else {
			best_score = int.MaxValue;
			foreach (var (card, (row, col)) in moves) {
				var move_record = state.make_move(row, col, card);
				if (move_record == null)
					continue;
				try {
					best_score = Math.Min(best_score, _solve_endgame_exact(state, ai_player_idx, cache));
				} finally {
					state.undo_move(move_record);
				}
			}
		}
		cache[key] = best_score;
		return best_score;
	}

	public class ConsoleSearchReporter(float interval = 0.5f) {
		//节流输出服务端搜索速度，避免刷屏拖慢搜索。
		public float interval = interval;
		public DateTime last_print = DateTime.MinValue;
		public int endgame_nodes;
		public DateTime? endgame_start;

		public class ProgressInfo {
			public string phase;
			public int depth;
			public int max_depth;
			public (Card, (int, int))? best_move;
			public float best_score;
			public int nodes_searched;
			public float time_elapsed;
			public float time_remaining;
			public float tt_hit_rate;
			public float cutoff_rate;
			public SearchStats.Summary stats = new();
		}

		//打印 Minimax 实时速度
		public void on_minimax_progress(ProgressInfo progress_info) {
			var now = DateTime.Now;
			if ((now - last_print).TotalSeconds < interval)
				return;
			last_print = now;
			var stats = progress_info.stats;
			var elapsed = Math.Max(progress_info.time_elapsed, 1e-6);
			var nodes = progress_info.nodes_searched;
			var nps = nodes / elapsed;
			println(
				$"[Search] depth={progress_info.depth} " +
				$"nodes={nodes:,} nps={nps:,.0f} " +
				$"tt={stats.tt_hit_rate * 100:.1f}% " +
				$"cutoff={stats.cutoff_rate * 100:.1f}% " +
				$"elapsed={elapsed:.2f}s");
		}

		//开始记录信息集残局速度
		public void start_endgame() {
			endgame_nodes = 0;
			endgame_start = DateTime.Now;
			last_print = DateTime.MinValue;
		}

		//打印信息集残局实时速度
		public void on_endgame_node(int move_index, int move_count, int scenario_index, int scenario_count) {
			endgame_nodes += 1;
			var now = DateTime.Now;
			if ((now - last_print).TotalSeconds < interval)
				return;
			last_print = now;
			var elapsed = Math.Max((now - (endgame_start ?? now)).TotalSeconds, 1e-6f);
			println(
				$"[Endgame] move={move_index}/{move_count} " +
				$"scenario={scenario_index}/{scenario_count} " +
				$"nodes={endgame_nodes:,} nps={endgame_nodes / elapsed:,.0f} " +
				$"elapsed={elapsed:.2f}s"
			);
		}
	}

	//计算对手对当前局面的最佳即时回应分数
	private static float _best_immediate_reply_score(GameState state_after_my_move, int ai_player_idx) {
		var reply_moves = state_after_my_move.get_available_moves();
		if (reply_moves.Count == 0)
			return evaluate_state(state_after_my_move, ai_player_idx);
		var best_reply = float.MaxValue;
		foreach (var (reply_card, (row, col)) in reply_moves) {
			var reply_state = (GameState)state_after_my_move.Clone();
			var scenario_card = reply_state.current_player.hand.FirstOrDefault(c => c.card_id == reply_card.card_id
			);
			if (scenario_card == null) continue;
			if (reply_state.make_move(row, col, scenario_card) == null) continue;
			best_reply = Math.Min(best_reply, evaluate_state(reply_state, ai_player_idx));
		}
		return best_reply != float.MaxValue ? best_reply : evaluate_state(state_after_my_move, ai_player_idx);
	}

	//对单个走法进行信息集残局评分
	private static (float, float, float, float) _evaluate_endgame_move_robustly(GameState base_state, (Card, (int, int)) move, List<GameState> scenario_states, int ai_player_idx,
		float safety_margin = 0.75f, ConsoleSearchReporter? progress_reporter = null,
		int move_index = 1, int move_count = 1) {
		var (card, (row, col)) = move;
		List<float> scenario_scores = [];
		var safety_votes = 0;
		var corner_risk = _calculate_corner_safety_risk(card, row, col, base_state.board, base_state.rules);
		Dictionary<(int current_player_idx, (int, string?, int, int, int, int, int)?[], (int, string?, bool, int, int, int, int, int)[][], string[]), int> exact_cache = [];
		var use_exact_solver = _board_occupancy(base_state.board) >= 5;
		var scenario_count = scenario_states.Count;
		for (var i = 0; i < scenario_states.Count; i++) {
			var scenario_index = i + 1;
			var scenario = scenario_states[i];
			if (progress_reporter != null)
				progress_reporter.on_endgame_node(move_index, move_count, scenario_index, scenario_count);
			var scenario_eval = evaluate_state(scenario, ai_player_idx);
			var scenario_after = (GameState)scenario.Clone();
			var scenario_card = scenario_after.current_player.hand.FirstOrDefault(c => c.card_id == card.card_id);
			if (scenario_card == null) continue;
			if (scenario_after.make_move(row, col, scenario_card) == null) continue;
			var reply_score = use_exact_solver ? _solve_endgame_exact(scenario_after, ai_player_idx, exact_cache) : _best_immediate_reply_score(scenario_after, ai_player_idx);
			scenario_scores.Add(reply_score);
			if (use_exact_solver && reply_score >= 0 ||
			    !use_exact_solver && reply_score >= scenario_eval - safety_margin)
				safety_votes += 1;
		}
		if (scenario_scores.Count == 0)
			return (float.MinValue, -0.0f, float.MinValue, corner_risk);
		var avg_score = scenario_scores.Sum() / scenario_scores.Count;
		var worst_score = scenario_scores.Min();
		var robust_score = avg_score * 0.45f + worst_score * 0.55f;
		var safety_ratio = safety_votes / scenario_scores.Count;
		if (!use_exact_solver && _is_corner_position(row, col) && corner_risk >= 5.0 && safety_ratio < 0.8)
			return (float.MinValue, safety_ratio, float.MinValue, corner_risk);
		var final_score = robust_score + safety_ratio * 2.0f - corner_risk * 0.15f;
		return (final_score, safety_ratio, robust_score, corner_risk);
	}

	public class ScoredMoves {
		public (Card, (int, int)) move;
		public float final_score;
		public float safety_ratio;
		public float robust_score;
		public float corner_risk;
	}

	//在残局场景中选择鲁棒性更强的走法
	public static ((Card, (int, int))?, List<ScoredMoves> ) select_endgame_robust_move(GameState base_state, List<GameState> scenario_states, int ai_player_idx, ConsoleSearchReporter? progress_reporter = null) {
		var moves = base_state.get_available_moves();
		if (moves.Count == 0)
			return (null, []);
		List<ScoredMoves> scored_moves = [];
		progress_reporter?.start_endgame();
		var move_count = moves.Count;
		for (var i = 0; i < scenario_states.Count; i++) {
			var move_index = i + 1;
			var move = moves[i];
			var (final_score, safety_ratio, robust_score, corner_risk) = _evaluate_endgame_move_robustly(
				base_state,
				move,
				scenario_states,
				ai_player_idx,
				progress_reporter: progress_reporter,
				move_index: move_index,
				move_count: move_count);
			scored_moves.Add(new ScoredMoves {
				move = move,
				final_score = final_score,
				safety_ratio = safety_ratio,
				robust_score = robust_score,
				corner_risk = corner_risk
			});
		}
		var fully_safe_moves = scored_moves.Where(item => item.safety_ratio >= 1.0).ToList();
		var safe_moves = fully_safe_moves.Count > 0
			? fully_safe_moves
			: scored_moves.Where(item => item.safety_ratio >= 0.75 && (
				item.corner_risk < 5.0 || item.safety_ratio >= 0.8)).ToList();
		var ranked_moves = safe_moves.Count > 0 ? safe_moves : scored_moves;
		ranked_moves = ranked_moves
			.OrderByDescending(item => item.safety_ratio)
			.ThenByDescending(item => item.final_score)
			.ThenByDescending(item => item.robust_score)
			.ThenBy(item => item.corner_risk)
			.ToList();
		var best_item = ranked_moves[0];
		return (best_item.move, scored_moves);
	}

	//判断是否启用信息集残局求解
	private static bool _should_use_endgame_robust_mode(Board board_state, int opp_unknown_count) {
		var occupied = _board_occupancy(board_state);
		return occupied >= 5 && opp_unknown_count <= 2;
	}

	private static (int, int, int) _move_key((Card, (int, int)) move) {
		var (card, (row, col)) = move;
		return (card.card_id, row, col);
	}

	private static bool _is_corner_position(int row, int col) => CORNER_SPAN.Contains((row, col));

	//计算残局角落安全风险
	private static float _calculate_corner_safety_risk(Card card, int row, int col, Board board_state, List<string> rules) {
		var occupied = _board_occupancy(board_state);
		if (occupied < 5) return 0;
		var stage_weight = 1 + Math.Min((occupied - 4) / 4f, 1);
		var risk = 0f;
		var attackable_sides = 0;
		var weak_sides = 0;
		(int, int, string, string)[] directions = [
			(-1, 0, "up", "down"),
			(1, 0, "down", "up"),
			(0, -1, "left", "right"),
			(0, 1, "right", "left")
		];
		foreach (var (dr, dc, my_dir, opp_dir) in directions) {
			var (nr, nc) = (row + dr, col + dc);
			if (nr < 0 || nr >= 3 || nc < 0 || nc >= 3) continue;
			attackable_sides += 1;
			var my_value = card.get_effective_value(my_dir, rules ?? []);
			if (my_value <= 3) {
				weak_sides += 1;
				risk += (4 - my_value) * 2.2f;
			} else if (my_value == 4)
				risk += 1.0f;
			var adj_card = board_state?.get_card(nr, nc);
			if (adj_card != null && adj_card.owner != card.owner) {
				var opp_value = adj_card.get_effective_value(opp_dir, rules ?? []);
				if (opp_value > my_value)
					risk += (opp_value - my_value) * 1.5f;
				else if (opp_value == my_value)
					risk += 1.0f;
			}
		}
		if (_is_corner_position(row, col) && weak_sides > 0)
			risk += weak_sides * 1.5f;
		else if (attackable_sides >= 3 && weak_sides > 0)
			risk += weak_sides * 1.0f;
		return risk * stage_weight;
	}

/*
 * 解析手牌，智能处理未知卡牌

    Args:
        hand_json: 手牌JSON数据
        owner: 卡牌所有者
        used_cards: 已使用的卡牌ID集合
        rules: 当前游戏规则（用于智能采样）
        board_state: 当前棋盘状态（用于智能采样）
        is_opponent: 是否为对手手牌（启用行为建模）
        id_offset: ID偏移量（已弃用）
        skip_sampling: 跳过智能采样，保留未知卡牌为占位符（蒙特卡洛求解器用）
 */
	public static List<Card> parse_hand(List<HandJsonItem> hand_json, string owner, HashSet<int> used_cards, List<string>? rules, Board? board_state = null, bool is_opponent = false, int id_offset = 1000, bool skip_sampling = false) {
		List<(string, object)> hand_slots = []; 
		List<Card> known_cards = [];
		var unknown_count = 0;
		var type_map = get_card_type_map();
		//第一遍：解析所有槽位，保留原始顺序
		foreach (var item in hand_json) {
			if (item is { numD: 0, numR: 0, numU: 0, numL: 0 }) {
				hand_slots.Add(("unknown", item));
				unknown_count += 1;
			} else {
				var (up, right, down, left) = (item.numU, item.numR, item.numD, item.numL); //Reverse
				var card_id = find_card_id_by_stats(up, right, down, left);
				if (card_id == null)
					throw new Exception($"Hand card not found in database: U{up} R{right} D{down} L{left}");
				var card_type = type_map[card_id.Value];
				var c = new Card(up, right, down, left, owner, card_id, card_type,
					item.canUse);
				hand_slots.Add(("known", c));
				known_cards.Add(c);
				used_cards.Add(card_id.Value);
			}
		}
		List<Card> generated_unknown_cards = [];
		//第二遍：智能处理未知卡牌（或跳过采样保留占位符）
		if (unknown_count > 0) {
			if (skip_sampling)
				println($"Skipped sampling: kept {unknown_count} unknown cards as placeholders for Monte Carlo");
			else {
				var card_type_label = is_opponent ? "opponent" : "own";
				println($"Processing {unknown_count} unknown {card_type_label} cards for {owner}");
				ensure_handler_initialized();
				var handler = UnknownCardHandler.get_unknown_card_handler();
				if (handler != null && rules is { Count: > 0 }) {
					var unknown_cards = is_opponent
						? handler.generate_opponent_cards(
							unknown_count,
							rules,
							[..used_cards],
							board_state,
							known_cards,
							owner,
							true)
						: handler.generate_unknown_cards(
							unknown_count,
							rules,
							[..used_cards],
							board_state,
							known_cards,
							owner,
							true);
					generated_unknown_cards = _select_unknown_cards_for_slots(
						unknown_cards,
						unknown_count,
						board_state,
						rules,
						owner,
						is_opponent
					);
					println($"Generated {generated_unknown_cards.Count} {card_type_label} cards for {unknown_count} unknown slots");
				} else {
					println("Using fallback sampling for unknown cards");
					var all_cards = get_all_cards();
					var sample_size = Math.Min(unknown_count * 5, all_cards.Count);
					var sampled_cards = all_cards.Sample(sample_size);
					if (sampled_cards.Count > unknown_count)
						sampled_cards = sampled_cards.Take(unknown_count).ToList();
					generated_unknown_cards = sampled_cards.Select(card => new Card(card.up, card.right, card.down, card.left, owner,
						card.card_id, card.card_type)).ToList();
					println($"Fallback generated {generated_unknown_cards.Count} cards for {unknown_count} unknown slots");
				}
			}
		}
		//第三遍：按原始顺序重建手牌
		List<Card> hand = [];
		var unknown_idx = 0;
		foreach (var (kind, payload) in hand_slots) {
			if (kind == "known")
				hand.Add((Card)payload);
			else {
				var payload2 = (HandJsonItem)payload;
				Card card;
				if (unknown_idx < generated_unknown_cards.Count)
					card = generated_unknown_cards[unknown_idx];
				else
					card = new Card(0, 0, 0, 0, owner, null, null, payload2.canUse);
				card.can_use = payload2.canUse;
				hand.Add(card);
				unknown_idx += 1;
			}
		}
		return hand;
	}

	public static (List<string> rules, string open_mode)
		parse_rules_and_open_mode(string rules_str) {
		//解码unicode

		List<string> rules = [];
		var open_mode = "none";
		// 规则识别
		println(rules_str);
		if (rules_str.Contains("全明牌"))
			open_mode = "all";
		else if (rules_str.Contains("三明牌"))
			open_mode = "three";
		if (rules_str.Contains("同数"))
			rules.Add("同数");
		if (rules_str.Contains("加算"))
			rules.Add("加算");
		if (rules_str.Contains("逆转"))
			rules.Add("逆转");
		if (rules_str.Contains("王牌杀手"))
			rules.Add("王牌杀手");
		if (rules_str.Contains("同类强化"))
			rules.Add("同类强化");
		if (rules_str.Contains("同类弱化"))
			rules.Add("同类弱化");
		if (rules_str.Contains("秩序"))
			rules.Add("秩序");
		if (rules_str.Contains("混乱"))
			rules.Add("混乱");
		if (rules_str.Contains("选拔"))
			rules.Add("选拔");
		// 允许最多4条规则
		// 可按逗号分割后去重
		return (rules, open_mode);
	}

	public static List<Card> complete_unknown_hand(List<Card?> hand, List<Card> all_cards, List<int> used_cards) {
		// hand: List[Card or None]
		List<Card> completed = [];
		foreach (var c in hand) {
			if (c != null) {
				completed.Add(c);
				if (c.card_id != -1)
					used_cards.Add(c.card_id);
			} else {
				// 用全牌池未用过的牌补全
				foreach (var card in all_cards)
					if (!used_cards.Contains(card.card_id) && card.owner == null) {
						completed.Add(new Card(card.up, card.right, card.down, card.left, null, card.card_id, card.card_type));
						used_cards.Add(card.card_id);
						break;
					}
			}
		}
		return completed;
	}

	//分析对手手牌
	public static OpponentHandAnalysis analyze_opponent_hand(List<Card>? opp_hand, List<string> rules, Board board) {
		if (opp_hand == null || opp_hand.Count == 0)
			return new() {
				predicted_cards = [],
				total_unknown = 0,
				strategy_analysis = "无对手手牌信息"
			};
		//统计已知和未知卡牌
		List<PredictedCard> known_cards = [];
		List<PredictedCard> predicted_cards = [];
		var unknown_count = 0;
		var star_map = get_card_star_map();
		foreach (var card in opp_hand) {
			// 检查是否是原始未知卡牌（所有数值为0）
			if (card is { up: 0, right: 0, down: 0, left: 0 }) {
				unknown_count += 1;
				continue;
			}
			//检查是否是智能采样生成的卡牌
			var star = star_map.TryGetValue(card.card_id, out var v) ? v.ToString() : "?";
			var confidence = 1.0f; // 默认已知卡牌
			var reasoning = "已知卡牌";
			//判断是否为预测卡牌的更准确方法
			var is_predicted = false;
			if (card.card_id != -1) {
				if (card.card_id >= 1000 || // 如果card_id >= 1000，这是预测卡牌
				    card._is_generated // 或者，如果这是从unknown_card_handler生成的卡牌
				   ) {
					is_predicted = true;
					unknown_count += 1; //统计为未知卡牌
					confidence = get_prediction_confidence(card, rules);
					reasoning = get_prediction_reasoning(card, rules);
				}
			}
			//生成卡牌显示信息
			var card_display = format_card_display(card, star);
			PredictedCard card_info = new() {
				card = card_display,
				star = star,
				confidence = confidence,
				reasoning = reasoning,
				is_predicted = is_predicted
			};
			if (is_predicted || confidence < 1.0)
				predicted_cards.Add(card_info);
			else
				known_cards.Add(card_info);
		}
		//分析未知卡牌策略
		var strategy_analysis = analyze_opponent_strategy(rules, board, known_cards);
		//选拔规则特殊分析
		string? draft_analysis = null;
		if (rules.Contains("选拔")) {
			draft_analysis = analyze_draft_mode_constraints(opp_hand, board, rules);
			if (draft_analysis != null)
				strategy_analysis += $" | 选拔分析：{draft_analysis}";
		}
		//如果还有未处理的未知卡牌，生成通用预测
		var remaining_unknown = unknown_count - predicted_cards.Count;
		if (remaining_unknown > 0) {
			var generic_predictions = predict_unknown_cards(rules, board, remaining_unknown);
			predicted_cards.AddRange(generic_predictions);
		}
		//合并所有卡牌信息
		var all_cards = known_cards.Concat(predicted_cards).ToList();
		OpponentHandAnalysis result = new() {
			predicted_cards = all_cards.Take(10).ToList(), // 最多显示10张
			total_unknown = unknown_count,
			strategy_analysis = strategy_analysis
		};
		//如果有选拔规则，添加星级分析
		if (rules.Contains("选拔") && draft_analysis != null)
			result.draft_analysis = draft_analysis;
		return result;
	}

	public static float get_prediction_confidence(Card card, List<string> rules) {
		//根据规则和卡牌特征计算预测置信度（支持多规则组合）
		var confidence = 0.6f; // 基础置信度
		List<float> rule_bonuses = []; // 记录各规则的置信度加成
		//选拔规则优先级最高
		if (rules.Contains("选拔"))
			//规则下，星级匹配的置信度很高
			rule_bonuses.Add(0.85f);
		//连携类规则优先级最高
		if (rules.Contains("同数") && has_same_number_potential(card)) {
			if (rules.Contains("加算") && has_addition_potential(card))
				// 同时适合同数和加算的卡牌置信度最高
				rule_bonuses.Add(0.9f);
			else
				rule_bonuses.Add(0.8f);
		} else if (rules.Contains("加算") && has_addition_potential(card))
			rule_bonuses.Add(0.7f);
		//逆转规则（使用有效数值）
		if (rules.Contains("逆转")) {
			int[] effective_values = [
				card.get_effective_value("up", []), card.get_effective_value("right", []),
				card.get_effective_value("down", []), card.get_effective_value("left", [])
			];
			var avg_value = effective_values.Sum() / 4f;
			if (avg_value <= 5) // 低数值卡牌在逆转规则下更有用
				rule_bonuses.Add(0.8f);
			else if (avg_value >= 8) // 高数值卡牌在逆转规则下不利
				rule_bonuses.Add(0.4f);
			else
				rule_bonuses.Add(0.6f);
		}
		// 王牌杀手规则（使用修正后数值）
		if (rules.Contains("王牌杀手")) {
			int[] effective_values = [
				card.get_modified_value("up"), card.get_modified_value("right"),
				card.get_modified_value("down"), card.get_modified_value("left")
			];
			var ace_killer_count = effective_values.Count(v => v == 1 || v == 10);
			if (ace_killer_count >= 2)
				rule_bonuses.Add(0.85f); // 多个1或A的卡牌
			else if (ace_killer_count == 1)
				rule_bonuses.Add(0.75f); // 单个1或A
			else
				rule_bonuses.Add(0.5f); // 无1或A
		}
		//同类强化/弱化规则
		if (rules.Contains("同类强化") || rules.Contains("同类弱化") && card.card_type != null) {
			if (rules.Contains("同类强化"))
				rule_bonuses.Add(0.7f); // 同类强化通常是好事
			else //同类弱化
				rule_bonuses.Add(0.5f); // 同类弱化是风险
		}
		//秩序/混乱规则影响
		if (rules.Contains("秩序") || rules.Contains("混乱")) {
			if (!card.can_use)
				rule_bonuses.Add(0.3f); // 不可使用的卡牌置信度很低
			else
				rule_bonuses.Add(0.6f); // 可使用的卡牌正常置信度
		}
		//计算最终置信度：取最高规则置信度，但有多规则加成
		if (rule_bonuses.Count > 0) {
			var base_confidence = rule_bonuses.Max();
			// 多规则组合加成：每多一条规则+0.05，最大不超过0.95
			var multi_rule_bonus = Math.Min(0.05f * (rule_bonuses.Count - 1), 0.15f);
			confidence = Math.Min(0.95f, base_confidence + multi_rule_bonus);
		}
		return confidence;
	}

	//根据规则和卡牌特征生成预测原因
	public static string get_prediction_reasoning(Card card, List<string> rules) {
		List<string> reasons = [];
		//选拔规则分析
		if (rules.Contains("选拔")) {
			var star_map = get_card_star_map();
			var star = star_map.TryGetValue(card.card_id, out var v) ? v.ToString() : "?";
			if (star == "5")
				reasons.Add("选拔模式下的5星王牌，战略价值极高");
			else if (star == "4")
				reasons.Add("选拔模式下的4星强力卡牌，关键时刻的选择");
			else if (star is "1" or "2")
				reasons.Add("选拔模式下的低星级卡牌，早期使用较安全");
			else
				reasons.Add("选拔模式下的中等星级卡牌，平衡性较好");
		}
		//连携类规则分析
		if (rules.Contains("同数") && has_same_number_potential(card)) {
			if (rules.Contains("加算") && has_addition_potential(card))
				reasons.Add("同时适合同数和加算连携的复合战术卡牌");
			else
				reasons.Add("同数规则下可能用于设置连携陷阱");
		} else if (rules.Contains("加算") && has_addition_potential(card))
			reasons.Add("加算规则下适合数值组合连携");
		//其他规则分析
		if (rules.Contains("逆转")) {
			int[] effective_values = [
				card.get_effective_value("up", []), card.get_effective_value("right", []),
				card.get_effective_value("down", []), card.get_effective_value("left", [])
			];
			var avg_value = effective_values.Sum() / 4f;
			if (avg_value <= 5)
				reasons.Add("逆转规则下的优势低数值卡牌");
			else
				reasons.Add("虽然数值较高，但可能用于特殊战术");
		}
		if (rules.Contains("王牌杀手")) {
			int[] effective_values = [
				card.get_modified_value("up"), card.get_modified_value("right"),
				card.get_modified_value("down"), card.get_modified_value("left")
			];
			if (effective_values.Contains(1) || effective_values.Contains(10))
				reasons.Add("王牌杀手规则下含1或A具有特殊效果");
			else
				reasons.Add("王牌杀手规则下的支援卡牌");
		}
		// 如果没有特殊规则匹配，使用通用分析
		if (reasons.Count == 0) {
			var star_map = get_card_star_map();
			var star = star_map.GetValueOrDefault(card.card_id, 3);
			if (star <= 2)
				reasons.Add("低星级卡牌，可能用于早期控场");
			else if (star >= 4)
				reasons.Add("高星级卡牌，关键时刻的强力选择");
			else
				reasons.Add("中等星级卡牌，平衡性较好");
		}
		return string.Join("; ", reasons);
	}

	//分析全局星级使用情况（包括棋盘和所有已知手牌）
	public static Dictionary<int, int> analyze_global_star_usage(Board? board, List<HandJsonItem> my_hand_json, List<HandJsonItem> opp_hand_json) {
		var star_map = get_card_star_map();
		var type_map = get_card_type_map();
		var global_usage = new Dictionary<int, int> {
			{ 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 }, { 5, 0 }
		};
		//统计棋盘卡牌
		if (board != null) {
			for (var r = 0; r < 3; r++) {
				for (var c = 0; c < 3; c++) {
					var card = board.get_card(r, c);
					if (card != null && card.card_id != -1) {
						var star = star_map.GetValueOrDefault(card.card_id, 1);
						global_usage[star] += 1;
					}
				}
			}
		}
		//统计己方已知手牌
		//统计对手已知手牌
		foreach (var item in my_hand_json.Concat(opp_hand_json)) {
			if (item is not { numD: 0, numR: 0, numU: 0, numL: 0 }) {
				var (up, right, down, left) = (item.numU, item.numL, item.numD, item.numR);
				var card_id = find_card_id_by_stats(up, right, down, left);
				if (card_id != null) {
					var star = star_map.GetValueOrDefault(card_id.Value, 1);
					global_usage[star] += 1;
				}
			}
		}
		return global_usage;
	}

	//分析选拔模式下的星级约束
	public static string analyze_draft_mode_constraints(List<Card> all_hand, Board? board, List<string> rules) {
		var star_map = get_card_star_map();
		//统计全局星级使用情况
		var global_star_usage = new Dictionary<int, int> {
			{ 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 }, { 5, 0 }
		};
		//统计棋盘上的卡牌
		if (board != null) {
			for (var r = 0; r < 3; r++) {
				for (var c = 0; c < 3; c++) {
					var card = board.get_card(r, c);
					if (card != null && card.card_id != -1) {
						var star = star_map.GetValueOrDefault(card.card_id, 1);
						global_star_usage[star] += 1;
					}
				}
			}
		}
		//统计手牌中的已知卡牌
		if (all_hand.Count > 0)
			foreach (var card in all_hand) {
				// 只统计有效的已知卡牌
				if (card.card_id != -1 && card.card_id > 0 &&
				    !(card is { up: 0, right: 0, down: 0, left: 0 })) {
					var star = star_map.GetValueOrDefault(card.card_id, 1);
					global_star_usage[star] += 1;
				}
			}
		//计算剩余配额
		Dictionary<int, int> star_limits = new() {
			{ 1, 2 },
			{ 2, 2 },
			{ 3, 2 },
			{ 4, 2 },
			{ 5, 2 }
		};
		Dictionary<int, int> remaining_quota = [];
		foreach (var p in star_limits) {
			var (star, limit) = (p.Key, p.Value);
			var used = global_star_usage.GetValueOrDefault(star, 0);
			var remaining = Math.Max(0, limit - used);
			remaining_quota[star] = remaining;
		}
		//生成分析报告
		List<string> analysis_parts = [];
		//检查是否有星级用完
		var exhausted_stars = remaining_quota.Where(p => p.Value == 0).Select(p => p.Key.ToString()).ToList();
		if (exhausted_stars.Count > 0)
			analysis_parts.Add($"{string.Join(',', exhausted_stars)}星已用完");
		//检查剩余高价值星级
		var high_value_remaining = remaining_quota.GetValueOrDefault(4, 0) + remaining_quota.GetValueOrDefault(5, 0);
		if (high_value_remaining > 0)
			analysis_parts.Add($"剩余{high_value_remaining}张高星卡牌");
		//检查可用星级分布
		var available_stars = remaining_quota.Where(p => p.Value > 0).Select(p => p.Key.ToString()).ToList();
		if (available_stars.Count > 0)
			analysis_parts.Add($"可用星级:{string.Join(',', available_stars)}");
		//战略建议
		if (remaining_quota.GetValueOrDefault(5, 0) > 0)
			analysis_parts.Add("对手可能保留5星卡牌作为杀招");
		else if (remaining_quota.GetValueOrDefault(4, 0) > 0)
			analysis_parts.Add("对手可能依赖4星卡牌");
		return analysis_parts.Count > 0 ? string.Join(" | ", analysis_parts) : "星级分布正常";
	}

	//分析对手策略
	public static string analyze_opponent_strategy(List<string> rules, Board board, List<PredictedCard> known_cards) {
		List<string> strategies = [];
		// 选拔规则优先分析
		if (rules.Contains("选拔"))
			strategies.Add("严格控制星级配额，注意高星卡牌的战略性使用");
		//检查连携类规则（最重要）
		if (rules.Contains("同数") && rules.Contains("加算"))
			strategies.Add("对手可能会同时利用同数和加算连携，设置复合陷阱");
		else if (rules.Contains("同数"))
			strategies.Add("对手可能会设置同数连携陷阱，注意重复数值卡牌");
		else if (rules.Contains("加算"))
			strategies.Add("对手可能会利用加算规则设置连携，注意互补数值组合");
		// 检查其他特殊规则
		if (rules.Contains("逆转"))
			strategies.Add("偏好低数值卡牌");
		if (rules.Contains("王牌杀手"))
			strategies.Add("重点使用含1或A的卡牌");
		if (rules.Contains("同类强化"))
			strategies.Add("集中使用同类型卡牌以触发强化效果");
		if (rules.Contains("同类弱化"))
			strategies.Add("避免使用相同类型，采用多样化策略");
		if (strategies.Count > 0)
			return "对手策略：" + string.Join("，", strategies);
		return "对手采用常规策略，平衡攻防";
	}

	//预测未知卡牌
	public static List<PredictedCard> predict_unknown_cards(List<string> rules, Board board, int count) {
		List<PredictedCard> predictions = [];
		//选拔规则优先处理
		if (rules.Contains("选拔"))
			predictions.Add(new() {
				card = "根据星级配额的战略性卡牌",
				confidence = 0.85,
				reasoning = "选拔模式下严格按照星级配额进行卡牌选择"
			});
		//基于规则生成预测（支持多规则组合）
		if (rules.Contains("同数") && rules.Contains("加算"))
			predictions.Add(new() {
				card = "同数+加算复合连携卡牌",
				confidence = 0.8,
				reasoning = "同时适合同数和加算规则的复合战术卡牌"
			});
		else if (rules.Contains("同数"))
			predictions.Add(new() {
				card = "重复数值卡牌",
				confidence = 0.7,
				reasoning = "同数规则下偏好设置连携陷阱"
			});
		else if (rules.Contains("加算"))
			predictions.Add(new() {
				card = "互补数值卡牌",
				confidence = 0.6,
				reasoning = "加算规则下偏好和数组合"
			});
		if (rules.Contains("逆转") && predictions.Count < count)
			predictions.Add(new() {
				card = "低数值卡牌",
				confidence = 0.8,
				reasoning = "逆转规则下低数值更有优势"
			});
		if (rules.Contains("王牌杀手") && predictions.Count < count)
			predictions.Add(new() {
				card = "含1或A的卡牌",
				confidence = 0.75,
				reasoning = "王牌杀手规则下1和A具有特殊效果"
			});
		//补充通用预测
		while (predictions.Count < count)

			predictions.Add(new() {
				card = "中等星级卡牌",
				confidence = 0.5,
				reasoning = "玩家通常使用平衡的卡组配置"
			});
		return predictions.Take(count).ToList();
	}

	//计算胜率
	public static WinProbability calculate_win_probability(GameState game_state, (Card, (int, int)) move, string my_owner) {
		// 当前局面评估
		var current_eval = evaluate_current_position(game_state, my_owner);
		//执行移动后的评估（使用make_move/undo_move）
		var (card, (row, col)) = move;
		var move_record = game_state.make_move(row, col, card);
		if (move_record == null)
			return new WinProbability {
				current = Math.Round(sigmoid(current_eval), 3),
				after_move = Math.Round(sigmoid(current_eval), 3),
				confidence = 0.5
			};
		try {
			var after_move_eval = evaluate_current_position(game_state, my_owner);

			//转换为胜率（简化计算）
			var current_prob = sigmoid(current_eval);
			var after_move_prob = sigmoid(after_move_eval);
			return new WinProbability {
				current = Math.Round(current_prob, 3),
				after_move = Math.Round(after_move_prob, 3),
				confidence = Math.Min(0.9, Math.Abs(after_move_eval) / 10 + 0.6) // 基于评估差值的置信度
			};
		} finally {
			//撤销移动
			game_state.undo_move(move_record);
		}
	}

	//将评估值转换为0-1之间的概率
	public static float sigmoid(float x) => 1f / (1 + MathF.Exp(-x / 5)); // 调整缩放因子

	//评估当前局面（简化版）
	public static float evaluate_current_position(GameState game_state, string my_owner) {
		var (red_count, blue_count) = game_state.count_cards();
		var basic_score = my_owner == "blue" ? blue_count - red_count : red_count - blue_count;
		//考虑位置因素
		var position_bonus = 0f;
		for (var r = 0; r < 3; r++) {
			for (var c = 0; c < 3; c++) {
				var card = game_state.board.get_card(r, c);
				if (card != null) {
					var weight = CORNER_SPAN.Contains((r, c)) ? 1.5f : (r, c) == (1, 1) ? 1.2f : 1.0f;
					if (card.owner == "blue" && my_owner == "blue" || card.owner == "red" && my_owner == "red")
						position_bonus += weight;
					else
						position_bonus -= weight;
				}
			}
		}
		return basic_score * 2 + position_bonus;
	}

	//生成移动建议
	public static Recommendation generate_move_recommendation(GameState game_state, (Card, (int, int)) move, string my_owner) {
		var (card, (row, col)) = move;
		//分析移动的价值
		var move_reasoning = analyze_move_value(game_state, move, my_owner);
		var strategic_value = analyze_strategic_value(game_state, move, my_owner);
		//生成备选方案（简化）
		List<AlternativeMove> alternative_moves = [];
		var current_player = game_state.players[game_state.current_player_idx];
		var playable_cards = current_player.get_playable_cards(game_state.rules);
		var available_positions = game_state.board.available_positions();
		//评估几个备选移动
		foreach (var alt_card in playable_cards.Take(2)) {
			// 只检查前2张卡
			foreach (var alt_pos in available_positions.Take(2)) {
				//只检查前2个位置
				if (alt_card.card_id != card.card_id && alt_pos != (row, col)) {
					var alt_eval = evaluate_alternative_move(game_state, (alt_card, alt_pos), my_owner);
					var star_map = get_card_star_map();
					var alt_star = star_map.TryGetValue(alt_card.card_id, out var v) ? v.ToString() : "?";
					alternative_moves.Add(new AlternativeMove {
						card = $"U{alt_card.up} R{alt_card.right} D{alt_card.down} L{alt_card.left} 星级:{alt_star}",
						pos = [alt_pos.Item1, alt_pos.Item2],
						value = Math.Round(alt_eval, 3)
					});
				}
			}
		}
		//按价值排序
		alternative_moves.Sort((a, b) => b.value.CompareTo(a.value));

		return new Recommendation {
			move_reasoning = move_reasoning,
			strategic_value = strategic_value,
			alternative_moves = alternative_moves.Take(3).ToList() // 最多3个备选
		};
	}

	//分析移动价值
	public static string analyze_move_value(GameState game_state, (Card, (int, int)) move, string my_owner) {
		var (card, (row, col)) = move;
		//检查能否吃掉对手卡牌（包括连携）
		var captured_cards = count_captured_cards(game_state, move, my_owner);
		var combo_analysis = analyze_combo_potential(game_state, move, my_owner);
		if (captured_cards > 0)
			if (combo_analysis != null)
				return $"此移动可以吃掉{captured_cards}张对手卡牌（{combo_analysis}）";
			else
				return $"此移动可以吃掉{captured_cards}张对手卡牌";
		//如果没有直接吃子，检查连携潜力
		if (combo_analysis != null)
			return $"设置连携陷阱：{combo_analysis}";
		//检查位置价值
		if (CORNER_SPAN.Contains((row, col)))
			return "占据角落位置，具有防御优势";
		if ((row, col) == (1, 1))
			return "控制中心位置，影响四个方向";
		return "稳定发展，保持场面控制";
	}

	//分析连携潜力和规则组合效果
	public static string? analyze_combo_potential(GameState game_state, (Card, (int, int)) move, string my_owner) {
		var (card, (row, col)) = move;
		var rules = game_state.rules;
		var board = game_state.board;
		List<string> analysis = [];
		(int, int, string, string)[] directions = [
			(-1, 0, "up", "down"),
			(1, 0, "down", "up"),
			(0, -1, "left", "right"),
			(0, 1, "right", "left")
		];
		//同数连携分析
		if (rules.Contains("同数")) {
			List<(int, int, int)> same_values = [];
			foreach (var (dr, dc, my_dir, opp_dir) in directions) {
				var (nr, nc) = (row + dr, col + dc);
				if (nr is >= 0 and < 3 && nc is >= 0 and < 3) {
					var neighbor_card = board.get_card(nr, nc);
					if (neighbor_card != null) {
						// 使用原始数值进行同数判断
						var my_value = card.get_base_value(my_dir);
						var neighbor_value = neighbor_card.get_base_value(opp_dir);
						if (my_value == neighbor_value)
							same_values.Add((nr, nc, my_value));
					}
				}
			}
			if (same_values.Count >= 2)
				analysis.Add($"同数连携：{same_values.Count}个数值{same_values[0].Item3}的相邻卡牌");
			else if (same_values.Count == 1)
				analysis.Add($"部分同数匹配：与({same_values[0].Item1},{same_values[0].Item2})数值{same_values[0].Item3}相同");
		}
		//加算连携分析
		if (rules.Contains("加算")) {
			Dictionary<int, List<(int, int)>> sum_groups = [];
			foreach (var (dr, dc, my_dir, opp_dir) in directions) {
				var (nr, nc) = (row + dr, col + dc);
				if (nr is >= 0 and < 3 && nc is >= 0 and < 3) {
					var neighbor_card = board.get_card(nr, nc);
					if (neighbor_card != null) {
						// 使用原始数值进行加算判断
						var my_value = card.get_base_value(my_dir);
						var neighbor_value = neighbor_card.get_base_value(opp_dir);
						var sum_val = my_value + neighbor_value;
						sum_groups.TryAdd(sum_val, []);
						sum_groups[sum_val].Add((nr, nc));
					}
				}
			}
			foreach (var p in sum_groups) {
				var sum_val = p.Key;
				var positions = p.Value;
				if (positions.Count >= 2)
					analysis.Add($"加算连携：{positions.Count}个和为{sum_val}的相邻卡牌");
				else if (positions.Count == 1)
					analysis.Add($"部分加算匹配：与({positions[0].Item1},{positions[0].Item2})和为{sum_val}");
			}
		}
		//逆转规则分析（使用有效数值，考虑同类修正）
		if (rules.Contains("逆转")) {
			int[] effective_values = [
				card.get_effective_value("up", []), card.get_effective_value("right", []),
				card.get_effective_value("down", []), card.get_effective_value("left", [])
			];
			var card_avg = effective_values.Sum() / 4f;
			if (card_avg <= 5)
				analysis.Add("逆转优势：低数值卡牌在逆转规则下更有竞争力");
			else if (card_avg >= 8)
				analysis.Add("逆转劣势：高数值卡牌在逆转规则下处于劣势");
		}
		//王牌杀手规则分析（使用修正后数值）
		if (rules.Contains("王牌杀手")) {
			int[] effective_values = [
				card.get_modified_value("up"), card.get_modified_value("right"),
				card.get_modified_value("down"), card.get_modified_value("left")
			];
			var ace_killer_count = effective_values.Count(v => v == 1 || v == 10);
			if (ace_killer_count >= 2)
				analysis.Add($"王牌杀手强势：卡牌有{ace_killer_count}个1或A，具有特殊攻击力");
			else if (ace_killer_count == 1)
				analysis.Add("王牌杀手效果：卡牌包含1或A，部分方向具有特殊效果");
		}
		//同类强化/弱化分析
		if (rules.Contains("同类强化") || rules.Contains("同类弱化") && card.card_type != null) {
			var same_type_count = 0;
			for (var r = 0; r < 3; r++) {
				for (var c = 0; c < 3; c++) {
					var board_card = board.get_card(r, c);
					if (board_card != null && board_card.card_type == card.card_type)
						same_type_count++;
				}
			}
			if (rules.Contains("同类强化") && same_type_count > 0)
				analysis.Add($"同类强化：棋盘上已有{same_type_count}张{card.card_type}类型卡牌，将获得数值提升");
			else if (rules.Contains("同类弱化") && same_type_count > 0)
				analysis.Add($"同类弱化：棋盘上已有{same_type_count}张{card.card_type}类型卡牌，将受到数值削弱");
		}
		// 复合规则效果分析
		var combo_effects = analyze_rule_combinations(rules, analysis);
		if (combo_effects.Count > 0)
			analysis.AddRange(combo_effects);
		return analysis.Count > 0 ? string.Join(": ", analysis) : null;
	}

	//分析复合规则效果
	public static List<string> analyze_rule_combinations(List<string> rules, List<string> existing_analysis) {
		List<string> combo_effects = [];
		//同数+加算复合连携
		if (rules.Contains("同数") && rules.Contains("加算")) {
			var has_same = existing_analysis.Any(a => a.Contains("同数连携"));
			var has_addition = existing_analysis.Any(a => a.Contains("加算连携"));
			if (has_same && has_addition)
				combo_effects.Add("复合连携：同时触发同数和加算效果");
		}
		//逆转+王牌杀手组合
		if (rules.Contains("逆转") && rules.Contains("王牌杀手"))
			combo_effects.Add("逆转王牌杀手：1和A在逆转规则下具有双重优势");
		//逆转+连携组合
		if (rules.Contains("逆转") && (rules.Contains("同数") || rules.Contains("加算")))
			combo_effects.Add("逆转连携：连携触发时使用逆转比较规则");
		//同类规则+连携组合
		if (rules.Contains("同类强化") || rules.Contains("同类弱化") && (rules.Contains("同数") || rules.Contains("加算")))
			combo_effects.Add("同类连携：卡牌数值变化影响连携计算");
		//秩序/混乱规则影响
		if (rules.Contains("秩序") && (rules.Contains("同数") || rules.Contains("加算")))
			combo_effects.Add("秩序连携：可用卡牌受限，连携策略需要重新评估");
		else if (rules.Contains("混乱") && (rules.Contains("同数") || rules.Contains("加算")))
			combo_effects.Add("混乱连携：可用卡牌受限，连携策略需要重新评估");
		return combo_effects;
	}

	//分析战略价值（支持多规则组合）
	public static string analyze_strategic_value(GameState game_state, (Card, (int, int)) move, string my_owner) {
		var (card, (row, col)) = move;
		var rules = game_state.rules;
		var board = game_state.board;
		List<string> strategies = [];
		//连携类规则策略
		if (rules.Contains("同数"))
			if (has_same_number_potential(card))
				strategies.Add("为同数连携做准备");
		if (rules.Contains("加算"))
			if (has_addition_potential(card))
				strategies.Add("为加算连携做准备");
		//逆转规则策略
		if (rules.Contains("逆转")) {
			var card_avg = (card.up + card.right + card.down + card.left) / 4f;
			if (card_avg <= 5)
				strategies.Add("逆转优势策略：使用低数值卡牌");
			else if (card_avg >= 8)
				strategies.Add("逆转风险策略：高数值卡牌需谨慎使用");
		}
		//王牌杀手规则策略
		if (rules.Contains("王牌杀手")) {
			int[] values = [card.up, card.right, card.down, card.left];
			var ace_killer_count = values.Count(v => v == 1 || v == 10);
			if (ace_killer_count >= 1)
				strategies.Add($"王牌杀手战术：利用{ace_killer_count}个特殊数值");
		}
		//同类强化/弱化策略
		if (rules.Contains("同类强化") || rules.Contains("同类弱化") && card.card_type != null) {
			var same_type_on_board = Enumerable.Range(0, 3)
				.SelectMany(r => Enumerable.Range(0, 3), (r, c) => (r, c))
				.Count(pos => board.get_card(pos.r, pos.c)?.card_type == card.card_type);


			if (rules.Contains("同类强化")) {
				if (same_type_on_board > 0)
					strategies.Add($"同类强化策略：与{same_type_on_board}张同类卡牌协同");
				else
					strategies.Add("同类强化先锋：率先建立类型优势");
			} else if (rules.Contains("同类弱化")) {
				if (same_type_on_board > 0)
					strategies.Add($"同类弱化规避：避免与{same_type_on_board}张同类卡牌聚集");
				else
					strategies.Add("同类弱化安全：首张同类卡牌不受影响");
			}
		}
		//秩序/混乱规则策略
		if (rules.Contains("秩序")) {
			if (card.can_use)
				strategies.Add("秩序策略：珍惜可用卡牌机会");
			else
				strategies.Add("秩序限制：当前卡牌不可使用");
		} else if (rules.Contains("混乱")) {
			if (card.can_use)
				strategies.Add("混乱策略：把握可用卡牌时机");
			else
				strategies.Add("混乱限制：当前卡牌不可使用");
		}
		//位置战略价值
		if ((row, col) == (1, 1))
			strategies.Add("控制中心战略位置");
		else if (CORNER_SPAN.Contains((row, col))) {
			//增强的边角战略评估
			var corner_analysis = analyze_corner_strategy(card, (row, col), board);
			if (corner_analysis != null)
				strategies.Add($"占据防御要塞：{corner_analysis}");
			else
				strategies.Add("占据防御要塞");
		} else if (EDGE_SPAN.Contains((row, col)))
			strategies.Add("控制边缘要道");
		//多规则组合策略
		var combo_strategies = analyze_multi_rule_strategies(rules, card, row, col);
		if (combo_strategies.Count > 0)
			strategies.AddRange(combo_strategies);

		if (strategies.Count > 0)
			return string.Join(": ", strategies);

		return "维持场面平衡，为后续发展铺路";
	}

	//分析多规则组合策略
	public static List<string> analyze_multi_rule_strategies(List<string> rules, Card card, int row, int col) {
		List<string> combo_strategies = [];
		//逆转+连携组合策略
		if (rules.Contains("逆转") && (rules.Contains("同数") || rules.Contains("加算"))) {
			var card_avg = (card.up + card.right + card.down + card.left) / 4f;
			if (card_avg <= 5 && (has_same_number_potential(card) || has_addition_potential(card)))
				combo_strategies.Add("逆转连携双重优势：低数值+连携潜力");
		}
		//王牌杀手+连携组合策略
		if (rules.Contains("王牌杀手") && (rules.Contains("同数") || rules.Contains("加算"))) {
			int[] values = [card.up, card.right, card.down, card.left];
			var has_ace_killer = values.Any(v => v == 1 || v == 10);
			if (has_ace_killer && (has_same_number_potential(card) || has_addition_potential(card)))
				combo_strategies.Add("王牌杀手连携：特殊数值+连携双重威胁");
		}
		//逆转+王牌杀手组合策略
		if (rules.Contains("逆转") && rules.Contains("王牌杀手")) {
			int[] values = [card.up, card.right, card.down, card.left];
			var has_ace_killer = values.Any(v => v == 1 || v == 10);
			if (has_ace_killer)
				combo_strategies.Add("逆转王牌杀手：1和A在逆转规则下无敌");
		}
		//同类规则+其他规则组合
		if (rules.Contains("同类强化") || rules.Contains("同类弱化") && card.card_type != null) {
			if (rules.Contains("逆转"))
				combo_strategies.Add("同类逆转策略：数值变化影响逆转效果");
			if (rules.Contains("王牌杀手")) {
				int[] values = [card.up, card.right, card.down, card.left];
				if (values.Any(v => v == 1 || v == 10))
					combo_strategies.Add("同类王牌杀手：数值变化不影响特殊效果");
			}
		}
		//中心位置的特殊组合价值
		if ((row, col) == (1, 1)) {
			var rule_count = rules.Count;
			if (rule_count >= 3)
				combo_strategies.Add($"多规则中心：{rule_count}条规则在中心位置发挥最大效果");
		}
		return combo_strategies;
	}

	//计算能吃掉的对手卡牌数量（包括连携效果）
	public static int count_captured_cards(GameState game_state, (Card, (int, int)) move, string my_owner) {
		var (card, (row, col)) = move;
		//记录原始卡牌归属
		Dictionary<(int, int), string> original_owners = [];
		for (var r = 0; r < 3; r++)
		for (var c = 0; c < 3; c++) {
			var board_card = game_state.board.get_card(r, c);
			if (board_card != null)
				original_owners[(r, c)] = board_card.owner;
		}
		//执行移动（会自动处理翻转和同类效果）
		var card_copy = (Card)card.Clone();
		card_copy.owner = my_owner == "red" ? "red" : "blue";
		var move_record = game_state.make_move(row, col, card_copy);
		if (move_record == null)
			return 0;
		try {
			// 计算被翻转的对手卡牌数量
			var captured = 0;
			for (var r = 0; r < 3; r++)
			for (var c = 0; c < 3; c++) {
				if (original_owners.ContainsKey((r, c))) {
					var board_card = game_state.board.get_card(r, c);
					if (board_card != null && original_owners[(r, c)] != card_copy.owner && board_card.owner == card_copy.owner)
						captured += 1;
				}
			}
			return captured;
		} finally {
			//撤销移动
			game_state.undo_move(move_record);
		}
	}

	//检查是否有同数潜力
	public static bool has_same_number_potential(Card card) {
		int[] values = [
			card.get_base_value("up"), card.get_base_value("right"),
			card.get_base_value("down"), card.get_base_value("left")
		];
		return values.Distinct().Count() < 4; // 有重复数值
	}

	//检查是否有加算潜力
	public static bool has_addition_potential(Card card) {
		int[] values = [
			card.get_base_value("up"), card.get_base_value("right"),
			card.get_base_value("down"), card.get_base_value("left")
		];
		// 检查是否有常见的和数组合
		for (var i = 0; i < values.Length; i++)
		for (var j = 0; j < values.Length; j++) {
			var v1 = values[i];
			var v2 = values[j];
			if (i != j && v1 + v2 is 8 or 10 or 12)
				return true;
		}
		return false;
	}

	//评估备选移动
	public static float evaluate_alternative_move(GameState game_state, (Card, (int, int)) move, string my_owner) {
		var (card, (row, col)) = move;
		var move_record = game_state.make_move(row, col, card);
		if (move_record == null)
			return evaluate_current_position(game_state, my_owner);
		try {
			return evaluate_current_position(game_state, my_owner);
		} finally {
			game_state.undo_move(move_record);
		}
	}

	//格式化卡牌显示信息，包含原始和修正后的数值
	public static string format_card_display(Card card, string star) {
		if (card.type_modifier != 0) {
			// 有同类修正的情况
			var modifier_str = $"{card.type_modifier:+d}";
			var base_display = $"U{card.base_up} R{card.base_right} D{card.base_down} L{card.base_left}";
			var modified_display = $"U{card.get_modified_value("up")} R{card.get_modified_value("right")} D{card.get_modified_value("down")} L{card.get_modified_value("left")}";
			var type_name = card.card_type ?? "无类型";
			return $"{base_display} → {modified_display} ({type_name}{modifier_str}) 星级:{star}";
		}
		// 无修正的情况
		return $"U{card.base_up} R{card.base_right} D{card.base_down} L{card.base_left} 星级:{star}";
	}

	//打印同类强化/弱化的详细分析
	private static void _print_type_analysis(GameState game_state) {
		//统计棋盘上已设置的各类型数量。手牌会受到修正影响，但不增加修正层数。
		Dictionary<string, int> board_type_counts = [];
		//棋盘卡牌
		println("棋盘卡牌类型分布：");
		for (var r = 0; r < 3; r++)
		for (var c = 0; c < 3; c++) {
			var card = game_state.board.get_card(r, c);
			if (card is { card_type: not null }) {
				board_type_counts[card.card_type] = board_type_counts.GetValueOrDefault(card.card_type, 0) + 1;
				var modifier_info = card.type_modifier != 0 ? $"修正{card.type_modifier:+d}" : "";
				println($"  位置({r},{c}): {card.card_type} {modifier_info}");
			}
		}
		//手牌类型
		println("\n手牌类型分布：");
		for (var i = 0; i < game_state.players.Length; i++) {
			var player = game_state.players[i];
			var player_name = i == 0 ? "红方" : "蓝方";
			println($"  {player_name}手牌：");
			foreach (var hand_card in player.hand) {
				if (hand_card.card_type != null) {
					var modifier_info = hand_card.type_modifier != 0 ? $"修正{hand_card.type_modifier:+d}" : "";
					var is_unknown = hand_card._is_prediction ||
					                 hand_card.up == 0 && hand_card.right == 0 && hand_card.down == 0 && hand_card.left == 0;
					var unknown_info = is_unknown ? " [推测]" : " [已知]";
					println($"    {hand_card.card_type}{modifier_info}{unknown_info}");
				}
			}
		}
		//总计
		println("\n类型总计：");
		foreach (var (card_type, count) in board_type_counts) {
			var rule_type = game_state.rules.Contains("同类强化") ? "强化" : "弱化";
			var modifier = game_state.rules.Contains("同类强化") ? count : -count;
			println($"  {card_type}: 场上{count}张 → {rule_type}{modifier:+d}");
		}
	}

	/*
	 *  分析边角放置战略价值
    针对高数值边角落放置的特殊评估
	 */
	public static string? analyze_corner_strategy(Card card, (int, int) position, Board board) {
		string[] directions = ["上", "右", "下", "左"];
		var (row, col) = position;
		int[] values = [card.up, card.right, card.down, card.left]; // U, R, D, L
		List<string> analysis_parts = [];
		//识别高数值边
		var high_values = values.Where(v => v >= 8).ToList();
		var very_high_values = values.Where(v => v >= 9).ToList();
		var ace_values = values.Where(v => v == 10).ToList(); // A值
		var low_values = values.Where(v => v <= 4).ToList();
		//特殊AA组合检测
		List<int> acePositions = [];
		for (var i = 0; i < values.Length; i++)
			if (values[i] == 10)
				acePositions.Add(i);
		if (acePositions.Count >= 2) {
			// 检查是否是最优AA组合
			if (row == 2 && col == 2 && acePositions.Contains(1) && acePositions.Contains(3)) // 右下角: R+L=AA
				analysis_parts.Add("最优AA右下角组合");
			else if (row == 2 && col == 0 && acePositions.Contains(1) && acePositions.Contains(0)) // 左下角: R+U=AA  
				analysis_parts.Add("次优AA左下角组合");
			else if (row == 0 && col == 2 && acePositions.Contains(2) && acePositions.Contains(3)) // 右上角: D+L=AA
				analysis_parts.Add("优质AA右上角组合");
			else if (row == 0 && col == 0 && acePositions.Contains(1) && acePositions.Contains(2)) // 左上角: R+D=AA
				analysis_parts.Add("平衡AA左上角组合");
			else if (acePositions.Count >= 2)
				analysis_parts.Add($"{acePositions.Count}边AA优势组合");
		}
		//三边高数值组合分析 (如右下左为9或8的卡牌)
		if (high_values.Count >= 3) {
			analysis_parts.Add(very_high_values.Count >= 3 ? "三边9+超强角落控制" : "三边8+强力角落控制");
			// 检查弱势边保护
			if (low_values.Count >= 1) {
				List<string> weak_sides = [];
				for (var i = 0; i < values.Length; i++) {
					var v = values[i];
					if (v <= 4) weak_sides.Add(directions[i]);
				}
				analysis_parts.Add($"保护{string.Join("/", weak_sides)}弱势边");
			}
		}
		//双边高数值分析
		else if (high_values.Count >= 2) {
			List<int> high_positions = [];
			for (var i = 0; i < values.Length; i++)
				if (values[i] >= 8)
					high_positions.Add(i);
			// 检查高数值边的相邻性
			if (are_adjacent_positions(high_positions)) {
				var side_names = get_side_names(high_positions);
				analysis_parts.Add($"相邻{string.Join("/", side_names)}高数值控制");
			} else {
				analysis_parts.Add("双边高数值防护");
			}
		}
		//分析当前位置的边暴露情况
		var exposed_sides = get_exposed_sides(row, col);
		if (exposed_sides.Count > 0) {
			List<int> exposed_values = [];
			foreach (var i in exposed_sides)
				exposed_values.Add(values[i]);

			if (exposed_values.Any(v => v <= 3)) {
				// 有弱势边暴露的警告
				var weak_exposed = exposed_sides
					.Where(i => values[i] <= 3)
					.Select(i => directions[i])
					.ToList();
				analysis_parts.Add($"警告：{string.Join("/", weak_exposed)}边存在弱点");
			} else if (exposed_values.All(v => v >= 8)) {
				// 所有暴露边都是高值
				List<string> strong_exposed = [];
				foreach (var i in exposed_sides)
					strong_exposed.Add(directions[i]);
				analysis_parts.Add($"{string.Join("/", strong_exposed)}边强力防护");
			}
		}
		//特殊情况：极端数值差异卡牌的角落适配性
		var (min_val, max_val) = (values.Min(), values.Max());
		if (max_val - min_val >= 7) // 如1,9,9,9这样的卡牌
			if (low_values.Count == 1) {
				var weak_side = directions[values.IndexOf(min_val)];
				analysis_parts.Add($"隐藏{weak_side}边弱点(值{min_val})");
			}
		//分析相邻已放置卡牌的协同效果
		var adjacent_synergy = analyze_adjacent_synergy(card, row, col, board);
		if (adjacent_synergy != null)
			analysis_parts.Add(adjacent_synergy);

		return analysis_parts.Count > 0 ? string.Join(", ", analysis_parts) : null;
	}

	//检查位置列表中是否有相邻的位置
	public static bool are_adjacent_positions(List<int> positions) {
		if (positions.Count < 2)
			return false;
		// 边的相邻关系: 0(上)-1(右), 1(右)-2(下), 2(下)-3(左), 3(左)-0(上)
		Dictionary<int, int[]> adjacency = new() {
			{ 0, [1, 3] },
			{ 1, [0, 2] },
			{ 2, [1, 3] },
			{ 3, [0, 2] }
		};
		for (var i = 0; i < positions.Count; i++) {
			var pos1 = positions[i];
			for (var j = i + 1; j < positions.Count; j++) {
				var pos2 = positions[j];
				if (adjacency[pos1].Contains(pos2))
					return true;
			}
		}
		return false;
	}

	//获取位置对应的边名称
	public static List<string> get_side_names(List<int> positions) {
		Dictionary<int, string> side_map = new() {
			{ 0, "上" },
			{ 1, "右" },
			{ 2, "下" },
			{ 3, "左" }
		};
		return positions.Select(pos => side_map[pos]).ToList();
	}

	//获取在该位置会暴露的边（不靠墙的边）
	public static List<int> get_exposed_sides(int row, int col) {
		List<int> exposed = [];
		// 检查每条边是否暴露
		if (row > 0) // 上边暴露
			exposed.Add(0);
		if (col < 2) // 右边暴露
			exposed.Add(1);
		if (row < 2) // 下边暴露
			exposed.Add(2);
		if (col > 0) // 左边暴露
			exposed.Add(3);
		return exposed;
	}

	//分析与相邻卡牌的协同效果
	public static string? analyze_adjacent_synergy(Card card, int row, int col, Board board) {
		List<string> synergy_effects = [];
		int[] values = [card.up, card.right, card.down, card.left];
		//检查四个方向的相邻卡牌
		(int, int, int, int)[] directions = [(-1, 0, 0, 2), (0, 1, 1, 3), (1, 0, 2, 0), (0, -1, 3, 1)];
		// (dr, dc, my_side, adj_side)
		foreach (var (dr, dc, my_side, adj_side) in directions) {
			var (nr, nc) = (row + dr, col + dc);
			if (0 <= nr && nr < 3 && 0 <= nc && nc < 3) {
				var adj_card = board.get_card(nr, nc);
				if (adj_card != null) {
					var my_value = values[my_side];
					var adj_value = adj_side switch {
						0 => adj_card.up,
						1 => adj_card.right,
						2 => adj_card.down,
						3 => adj_card.left
					};
					//检查数值匹配度
					if (my_value == adj_value && my_value >= 8) {
						var side_name = new[] { "上", "右", "下", "左" }[my_side];
						synergy_effects.Add($"{side_name}边与邻牌高值匹配({my_value})");
					} else if (my_value + adj_value == 10 && Math.Min(my_value, adj_value) >= 4) {
						var side_name = new[] { "上", "右", "下", "左" }[my_side];
						synergy_effects.Add($"{side_name}边与邻牌互补({my_value}+{adj_value}=10)");
					}
				}
			}
		}
		return synergy_effects.Count > 0 ? string.Join(", ", synergy_effects) : null;
	}

	//格式化移动用于显示
	public static string format_move_for_display((Card, (int, int))? move) {
		if (move == null) return "无移动";
		var (card, (row, col)) = move.Value;
		var star_map = get_card_star_map();
		var star = star_map.TryGetValue(card.card_id, out var v) ? v.ToString() : "?";
		return $"U{card.up}R{card.right}D{card.down}L{card.left}(★{star}) → ({row},{col})";
	}

	public class SearchSummary {
		public int max_depth_reached;
		public int total_nodes_searched;
		public float total_time_seconds;
		public float final_evaluation_score;
		public string score_trend;
		public string search_efficiency;
		public float average_branching_factor;
		public string transposition_table_hit_rate;
		public string alpha_beta_cutoff_rate;
	}

	//生成搜索摘要
	public static SearchSummary? generate_search_summary(List<SearchProgressData> progress_data) {
		if (progress_data.Count == 0) return null; //"无搜索数据"
		var final_data = progress_data[^1];
		var max_depth = final_data.depth;
		var total_nodes = final_data.nodes_searched;
		var total_time = final_data.time_elapsed;
		var final_score = final_data.best_score;
		// 计算评分变化趋势
		var score_trend = "稳定";
		if (progress_data.Count >= 2) {
			List<float> score_changes = [];
			for (var i = 1; i < progress_data.Count; i++) {
				var change = progress_data[i].best_score - progress_data[i - 1].best_score;
				score_changes.Add(change);
			}
			var avg_change = score_changes.Sum() / score_changes.Count;
			if (avg_change > 0.1)
				score_trend = "上升";
			else if (avg_change < -0.1)
				score_trend = "下降";
		}
		//计算搜索效率
		var efficiency = final_data.nodes_per_second > 1000 ? "高效" : final_data.nodes_per_second > 500 ? "正常" : "较慢";

		return new SearchSummary {
			max_depth_reached = max_depth,
			total_nodes_searched = total_nodes,
			total_time_seconds = total_time,
			final_evaluation_score = final_score,
			score_trend = score_trend,
			search_efficiency = efficiency,
			average_branching_factor = final_data.branching_factor,
			transposition_table_hit_rate = $"{final_data.tt_hit_rate}%",
			alpha_beta_cutoff_rate = $"{final_data.cutoff_rate}%"
		};
	}

	public static string[] ascii_text = [
		"████████╗████████╗ ██████╗    ███████╗██╗██████╗ ███████╗███╗   ██╗",
		"╚══██╔══╝╚══██╔══╝██╔════╝    ██╔════╝██║██╔══██╗██╔════╝████╗  ██║",
		"   ██║      ██║   ██║         ███████╗██║██████╔╝█████╗  ██╔██╗ ██║",
		"   ██║      ██║   ██║         ╚════██║██║██╔══██╗██╔══╝  ██║╚██╗██║",
		"   ██║      ██║   ╚██████╗    ███████║██║██║  ██║███████╗██║ ╚████║",
		"   ╚═╝      ╚═╝    ╚═════╝    ╚══════╝╚═╝╚═╝  ╚═╝╚══════╝╚═╝  ╚═══╝",
		"",
		"                                                Triple Triad Solver"
	];
	public static char[] particles = ['.', ',', ':', '*', '+', '·', ' '];

	public static string color_char(char ch, int x, float width) {
		const int r = 255;
		var g = (int)(180 - 120 * (x / width));
		var b = (int)(200 - 80 * (x / width));
		return $"\033[38;2;{r};{g};{b}m{ch}\033[0m";
	}

	public static string render(float frame) {
		List<string> output = [];
		foreach (var line in ascii_text) {
			var new_line = "";
			float width = line.Length;
			for (var i = 0; i < line.Length; i++) {
				var ch = line[i];

				var decay_chance = Math.Max(0, (i - width * 0.6f) / (width * 0.4f));

				if (ch != ' ' && Random.Shared.NextSingle() < decay_chance + frame * 0.02)
					new_line += particles[i];
				else
					new_line += color_char(ch, i, width);
			}
			output.Add(new_line);
		}
		return string.Join("\n", output);
	}
}
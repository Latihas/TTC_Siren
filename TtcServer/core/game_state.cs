using System;
using System.Collections.Generic;
using System.Linq;

namespace TtcServer.core;

public class MoveRecord(int row, int col, Card card, int player_idx, List<(int, int, string)> flipped_cards, Dictionary<string, int> type_modifiers, string card_original_owner) {
	public int row = row;
	public int col = col;
	public Card card = card; // 放置的卡牌
	public int player_idx = player_idx; //执行移动的玩家索引
	public List<(int, int, string)> flipped_cards = flipped_cards; // [(row, col, original_owner), ...]
	public Dictionary<string, int> type_modifiers = type_modifiers; // {card_id: original_modifier, ...}
	public string card_original_owner = card_original_owner;
}

/*
 *   幻卡游戏状态类，包含牌桌、双方玩家、当前回合玩家、规则。
 *  严格按照官方规则进行胜负判定。
 *  现在支持make_move/undo_move机制以避免深拷贝。
 */
public class GameState(Board board, Player[] players, int current_player_idx, List<string>? rules = null) : ICloneable {
	public Board board = board; //当前牌桌
	public Player[] players = players; //[红方玩家, 蓝方玩家]，约定0为红，1为蓝
	public int current_player_idx = current_player_idx; //当前回合玩家索引（0或1）
	public List<string> rules = rules ?? []; //当前规则列表
	public List<MoveRecord> move_history = []; //移动历史，用于undo
	public Player current_player => players[current_player_idx];
	public Player opponent_player => players[1 - current_player_idx];

	/*
	  return GameState(copy.deepcopy(self.board), [copy.deepcopy(p) for p in self.players], self.current_player_idx, list(self.rules))
	*/
	public object Clone() => new GameState((Board)board.Clone(), players.ToArray(), current_player_idx, rules.ToList());

	//判断游戏是否结束（牌桌满）。
	public bool is_game_over() {
		return Enumerable.Range(0, 3).All(row =>
			Enumerable.Range(0, 3).All(col =>
				board.grid[row][col] != null));
	}

	//统计红蓝双方拥有的卡牌总数（包括牌桌和手牌）。
	public (int, int) count_cards() {
		var red_count = 0;
		var blue_count = 0;
		//统计牌桌
		for (var r = 0; r < 3; r++)
		for (var c = 0; c < 3; c++) {
			var card = board.get_card(r, c);
			if (card != null)
				red_count++;
			else
				blue_count++;
		}
		//统计手牌
		foreach (var p in players) {
			foreach (var card in p.hand) {
				if (card.owner == "red")
					red_count++;
				else
					blue_count++;
			}
		}
		return (red_count, blue_count);
	}

	/*
	 *  判断胜负，返回胜者名称，平局返回None。
	 *  规则：9格占满后，包括后手未打出的那张卡在内，拥有更多卡牌的人胜。
	 */
	public string? get_winner() {
		var (red_count, blue_count) = count_cards();
		if (!is_game_over()) return null;
		if (red_count > blue_count)
			return players[0].name; //红方胜
		if (blue_count > red_count)
			return players[1].name; //蓝方胜
		return null; //平局
	}

/*
 *  获取当前玩家的所有可用动作
 *    返回: [(card, (row, col)), ...]
 */
	public List<(Card, (int, int))> get_available_moves() {
		List<(Card, (int, int))> moves = [];
		var playable_cards = current_player.get_playable_cards(rules);
		foreach (var card in playable_cards)
			for (var row = 0; row < 3; row++)
			for (var col = 0; col < 3; col++)
				if (board.is_empty(row, col))
					moves.Add((card, (row, col)));
		return moves;
	}
	/*
	 * 	执行移动并返回移动记录，用于后续undo
	 * 	这是新的高性能移动执行方法
	 */

	public MoveRecord? make_move(int row, int col, Card card) {
		//检查移动是否有效
		var hand_card = current_player.hand.FirstOrDefault(c => c.card_id == card.card_id);
		if (!board.is_empty(row, col) || hand_card == null) return null;
		//记录原始类型修正值（用于undo）
		var original_type_modifiers = new Dictionary<string, int>();
		if (rules.Contains("同类强化") || rules.Contains("同类弱化")) {
			//记录所有卡牌的当前type_modifier
			for (var r = 0; r < 3; r++) {
				for (var c = 0; c < 3; c++) {
					var board_card = board.get_card(r, c);
					if (board_card != null)
						original_type_modifiers[$"board_{r}_{c}"] = board_card.type_modifier;
				}
			}
			for (var i = 0; i < players.Length; i++) {
				var player = players[i];
				for (var j = 0; j < player.hand.Count; j++) {
					var hand_card_item = player.hand[j];
					original_type_modifiers[$"hand_{i}_{j}"] = hand_card_item.type_modifier;
				}
			}
		}
		var card_original_owner = card.owner;
		//设置卡牌所有者并放置
		card.owner = current_player_idx == 0 ? "red" : "blue";
		board.place_card(row, col, card);
		current_player.play_card(hand_card);
		//记录翻转的卡牌（用于undo）
		var flipped_cards = new List<(int, int, string)>();
		resolve_flip_with_record(row, col, card, flipped_cards);
		//同类强化/弱化处理
		if (rules.Contains("同类强化") || rules.Contains("同类弱化")) apply_same_type_effect(card);
		//创建移动记录
		var move_record = new MoveRecord(
			row,
			col,
			card,
			current_player_idx,
			flipped_cards,
			original_type_modifiers,
			card_original_owner
		);
		//切换玩家
		current_player_idx = 1 - current_player_idx;
		move_history.Add(move_record);
		return move_record;
	}

/*
 * 	撤销移动，恢复到移动前的状态
 *		如果不提供move_record，则撤销最后一次移动
 */
	public bool undo_move(MoveRecord? move_record = null) {
		if (move_record == null) {
			if (move_history.Count == 0) return false;
			move_record = move_history.Last();
			move_history.Remove(move_record);
		} else {
			//从历史中移除指定记录
			if (move_history.Contains(move_record))
				move_history.Remove(move_record);
		}
		//恢复玩家索引
		current_player_idx = move_record.player_idx;
		//恢复翻转的卡牌
		foreach (var (flipped_row, flipped_col, original_owner) in move_record.flipped_cards) {
			var flipped_card = board.get_card(flipped_row, flipped_col);
			if (flipped_card != null)
				flipped_card.owner = original_owner;
		}
		//移除放置的卡牌，恢复到手牌
		board.remove_card(move_record.row, move_record.col);
		current_player.hand.Add(move_record.card);
		move_record.card.owner = move_record.card_original_owner;
		//恢复类型修正值
		if (move_record.type_modifiers.Count > 0) {
			//恢复棋盘卡牌的type_modifier
			foreach (var p in move_record.type_modifiers) {
				var key = p.Key;
				var original_modifier = p.Value;
				if (key.StartsWith("board_")) {
					var parts = key.Split("_");
					var r = int.Parse(parts[1]);
					var c = int.Parse(parts[2]);
					var board_card = board.get_card(r, c);
					if (board_card != null)
						board_card.type_modifier = original_modifier;
				} else if (key.StartsWith("hand_")) {
					var parts = key.Split("_");
					var player_idx = int.Parse(parts[1]);
					var hand_idx = int.Parse(parts[2]);
					if (player_idx < players.Length && hand_idx < players[player_idx].hand.Count)
						players[player_idx].hand[hand_idx].type_modifier = original_modifier;
				}
			}
		}
		return true;
	}

/*
 *  翻面判定，支持基础规则、加算、同数及连锁。
 *      现在也支持逆转和王牌杀手规则。
 *      同时记录翻转的卡牌用于undo操作。
 */
	private void resolve_flip_with_record(int row, int col, Card card, List<(int, int, string)> flipped_cards, HashSet<(int, int)>? flipped_set = null, bool chain_only = false) {
		if (flipped_set == null) flipped_set = [];
		(int, int, string, string)[] directions = [
			(-1, 0, "up", "down"),
			(1, 0, "down", "up"),
			(0, -1, "left", "right"),
			(0, 1, "right", "left")
		];
		var owner = card.owner;
		var board = this.board;
		var to_flip = new Dictionary<(int, int), string>(); //{(nr, nc): reason}

		void add_flip(int target_row, int target_col, string reason) {
			if (to_flip.TryGetValue((target_row, target_col), out var current_reason) && current_reason is "same" or "plus") return;
			to_flip[(target_row, target_col)] = reason;
		}

		//--- 基础规则（包含逆转和王牌杀手）---
		foreach (var (dr, dc, my_dir, opp_dir) in directions) {
			var nr = row + dr;
			var nc = col + dc;
			if (nr is >= 0 and < 3 && nc is >= 0 and < 3) {
				var opp_card = board.get_card(nr, nc);
				if (opp_card != null && opp_card.owner != owner) {
					//使用新的比较方法
					var result = card.compare_values(my_dir, opp_card, opp_dir, rules);
					if (result == 1) add_flip(nr, nc, "base"); //我方获胜
				}
			}
		}
		if (!chain_only) {
			//--- 加算规则（修正版） ---
			if (rules.Contains("加算")) {
				List<(int, int, Card, int)> plus_list = []; // [(nr, nc, opp_card, 和)]
				var sum_map = new Dictionary<int, List<(int, int, Card)>>();
				foreach (var (dr, dc, my_dir, opp_dir) in directions) {
					var nr = row + dr;
					var nc = col + dc;
					if (nr is >= 0 and < 3 && nc is >= 0 and < 3) {
						var opp_card = board.get_card(nr, nc);
						if (opp_card != null) {
							//加算使用当前有效数值，但不应用逆转或王牌杀手。
							var my_value = card.get_effective_value(my_dir, rules);
							var opp_value = opp_card.get_effective_value(opp_dir, rules);
							var s = my_value + opp_value;
							plus_list.Add((nr, nc, opp_card, s));
							sum_map.TryAdd(s, []);
							sum_map[s].Add((nr, nc, opp_card));
						}
					}
				}
				//找出出现次数>=2的和
				var valid_sums = sum_map.Where(p => p.Value.Count >= 2).Select(p => p.Key).ToArray();
				// 至少有一个是敌方卡
				foreach (var s in valid_sums) {
					if (sum_map[s].Any(p => p.Item3.owner != owner))
						foreach (var (nr, nc, opp_card) in sum_map[s]) {
							if (opp_card.owner != owner) add_flip(nr, nc, "plus");
						}
				}
			}
			//--- 同数规则 ---
			if (rules.Contains("同数")) {
				List<(int, int, Card)> same_list = []; //[(nr, nc, opp_card)]
				foreach (var (dr, dc, my_dir, opp_dir) in directions) {
					var nr = row + dr;
					var nc = col + dc;
					if (nr is >= 0 and < 3 && nc is >= 0 and < 3) {
						var opp_card = board.get_card(nr, nc);
						if (opp_card != null) {
							//同数使用当前有效数值，但不应用逆转或王牌杀手。
							var my_value = card.get_effective_value(my_dir, rules);
							var opp_value = opp_card.get_effective_value(opp_dir, rules);
							if (my_value == opp_value) same_list.Add((nr, nc, opp_card));
						}
					}
				}
				//至少两次且至少有一次是对方卡
				if (same_list.Count >= 2 && same_list.Any(p => p.Item3.owner != owner)) {
					foreach (var (nr, nc, opp_card) in same_list) {
						if (opp_card.owner != owner) add_flip(nr, nc, "same");
					}
				}
			}
		}
		//执行翻转并递归连锁
		foreach (var (key, reason) in to_flip) {
			var (nr, nc) = key;
			if (flipped_set.Contains((nr, nc))) continue; //已翻转过，避免死循环
			var opp_card = board.get_card(nr, nc);
			if (opp_card != null && opp_card.owner != owner) {
				//记录原始所有者（用于undo）
				var original_owner = opp_card.owner;
				flipped_cards.Add((nr, nc, original_owner));
				//执行翻转
				opp_card.owner = owner;
				flipped_set.Add((nr, nc));
				//连锁：只有同数/加算取得的卡牌可以继续按基础比较触发连携。
				if (reason is "same" or "plus") {
					resolve_flip_with_record(nr, nc, opp_card, flipped_cards, flipped_set, true);
				}
			}
		}
	}

	//兼容性方法，使用make_move实现
	public bool play_move(int row, int col, Card card) => make_move(row, col, card) != null;

	/*
	 * 应用同类强化/弱化效果
	 *	智能计算每张卡牌应该受到的修正值
	 */
	public void apply_same_type_effect(Card played_card) {
		//无类型的卡牌不触发同类效果
		if (played_card.card_type == null) return;
		//重新计算所有卡牌的同类修正值
		recalculate_type_modifiers();
	}

	/*
	 * 重新计算所有卡牌的同类强化/弱化修正值。
	 * 场上已设置的同类型卡牌数量决定修正值，棋盘和手牌中的同类型卡牌都会受到影响。
	 */
	public void recalculate_type_modifiers() {
		if (!rules.Contains("同类强化") && !rules.Contains("同类弱化")) return;
		// 统计每种类型在棋盘上已经设置的数量
		var type_counts = new Dictionary<string, int>();
		for (var r = 0; r < 3; r++) {
			for (var c = 0; c < 3; c++) {
				var board_card = board.get_card(r, c);
				if (board_card is { card_type: not null }) {
					type_counts[board_card.card_type] = type_counts.GetValueOrDefault(board_card.card_type, 0) + 1;
				}
			}
		}
		//计算修正值并应用
		foreach (var (card_type, count) in type_counts) {
			var modifier = 0;
			if (rules.Contains("同类强化")) {
				//同类强化：场上每有一张同类型卡牌，该类型所有卡牌+1
				modifier = count;
			} else if (rules.Contains("同类弱化")) {
				//同类弱化：场上每有一张同类型卡牌，该类型所有卡牌-1
				modifier = -count;
			}
			//应用到棋盘上的同类型卡牌
			for (var r = 0; r < 3; r++) {
				for (var c = 0; c < 3; c++) {
					var board_card = board.get_card(r, c);
					if (board_card != null && board_card.card_type == card_type) {
						board_card.apply_type_modifier(modifier);
					}
				}
			}
			//应用到手牌中的同类型卡牌
			foreach (var player in players) {
				foreach (var hand_card in player.hand) {
					if (hand_card.card_type == card_type) {
						hand_card.apply_type_modifier(modifier);
					}
				}
			}
		}
	}

	/*
	 * 基于实际卡牌数据库统计来推测未知手牌的类型分布。
	使用全卡牌数据库中各类型的占比作为先验概率，
	结合蒙特卡洛采样思想给出期望类型计数。
	 */
	private Dictionary<string, int> _estimate_unknown_hand_types(Dictionary<string, int> type_counts) {
		//统计未知卡牌数量（仅计算真正未知的占位卡，而非AI预测卡）
		var unknown_count = 0;
		foreach (var player in players) {
			foreach (var hand_card in player.hand) {
				if (hand_card is { up: 0, right: 0, down: 0, left: 0 }) {
					unknown_count++;
				}
			}
		}
		if (unknown_count == 0) return type_counts;
		//获取全卡牌数据库的类型分布（懒加载缓存）
		var type_distribution = _get_card_type_distribution();
		if (type_distribution == null) return type_counts;
		var estimated_counts = new Dictionary<string, int>(type_counts);
		var total_cards = type_distribution.Values.Sum();
		foreach (var (card_type, count_in_db) in type_distribution) {
			var p_type = 1f * count_in_db / total_cards;
			// 期望 = 未知卡牌数量 × 该类型在数据库中的占比
			var expected_additional = 1f * unknown_count * p_type;
			//同类强化/弱化规则调整
			if (rules.Contains("同类强化")) {
				//玩家倾向于使用已有类型
				var current = type_counts.GetValueOrDefault(card_type, 0);
				if (current > 0) expected_additional *= 1.5f; //已有类型更可能出现
				else expected_additional *= 0.7f; //新类型不太可能出现
			} else if (rules.Contains("同类弱化")) {
				//玩家避免同类型聚集
				var current = type_counts.GetValueOrDefault(card_type, 0);
				if (current > 0) expected_additional *= 0.5f; //已有类型不太可能再出现
				else expected_additional *= 1.2f; //新类型更可能
			}
			// 只保留有意义的期望
			if (expected_additional >= 0.5f) {
				var current_count = type_counts.GetValueOrDefault(card_type, 0);
				estimated_counts[card_type] = Math.Max(current_count, current_count + (int)Math.Round(expected_additional));
			}
		}
		return estimated_counts;
	}

	private static Dictionary<string, int>? _type_distribution_cache;

	//获取全卡牌数据库的类型分布（懒加载缓存）
	private static Dictionary<string, int> _get_card_type_distribution() {
		if (_type_distribution_cache != null) {
			return _type_distribution_cache;
		}
		try {
			//尝试多个可能的路径
			var distribution = new Dictionary<string, int>();
			foreach (var record in AiServer.get_card_db()) {
				var card_type = record.TripleTriadCardType;
				if (!string.IsNullOrEmpty(card_type)) {
					distribution[card_type] = distribution.GetValueOrDefault(card_type, 0) + 1;
				}
			}
			_type_distribution_cache = distribution;
			return distribution;
		} catch {
			_type_distribution_cache = [];
			return [];
		}
	}
}
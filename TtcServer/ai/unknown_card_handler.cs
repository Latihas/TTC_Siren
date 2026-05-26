/*
智能未知牌处理模块
根据游戏规则、已知信息和统计分析来合理处理未知手牌
*/

//智能未知牌处理器

using System;
using System.Collections.Generic;
using System.Linq;
using TtcServer.core;
using static TtcServer.config.UnknownCardConfig;
using static TtcServer.Utils;
using Math = System.Math;

public class UnknownCardHandler {
	private List<Card> all_cards;
	private Dictionary<int, string?> card_type_map;
	private Dictionary<int, int> card_star_map;
	private Dictionary<int, List<int>> cards_by_star;
	private Dictionary<string, List<int>> cards_by_type;
	private Dictionary<int, int> value_distribution;

	private UnknownCardHandler(List<Card> all_cards, Dictionary<int, string?> card_type_map, Dictionary<int, int> card_star_map) {
		this.all_cards = all_cards;
		this.card_type_map = card_type_map;
		this.card_star_map = card_star_map;
		//预计算统计信息
		_compute_card_statistics();
	}

	private void _compute_card_statistics() {
		//预计算卡牌统计信息
		cards_by_star = [];
		cards_by_type = [];
		value_distribution = [];
		foreach (var card in all_cards) {
			var star = card_star_map.GetValueOrDefault(card.card_id, 1);
			var card_type = card_type_map!.GetValueOrDefault(card.card_id, null);
			cards_by_star.TryAdd(star, []);
			cards_by_star[star].Add(card.card_id);
			if (card_type != null) {
				cards_by_type.TryAdd(card_type, []);
				cards_by_type[card_type].Add(card.card_id);
			}
			//统计数值分布
			foreach (var value in new[] { card.up, card.right, card.down, card.left }) {
				value_distribution.TryAdd(value, 0);
				value_distribution[value]++;
			}
		}
	}

	/*
	 *  根据规则和游戏状态生成未知卡牌的合理估计
        现在支持动态采样数量调整

        Args:
            count: 需要生成的卡牌数量
            rules: 当前游戏规则
            used_cards: 已使用的卡牌ID集合
            board_state: 当前棋盘状态
            known_hand: 已知的手牌
            owner: 卡牌所有者
            can_use: 是否可用（秩序/混乱规则）
	 */
	public List<Card> generate_unknown_cards(int count, List<string> rules, HashSet<int> used_cards, Board? board_state = null, List<Card>? known_hand = null, string? owner = null, bool can_use = true) {
		//动态调整采样数量 - 选拔模式下使用精确采样
		var config = UnknownCardConfig.get_sampling_config();
		var max_cards = UnknownCardConfig.get_max_cards_per_unknown();
		//选拔模式下使用精确采样，不生成额外卡牌
		int actual_samples;
		List<Card> available_cards;
		if (rules.Contains("选拔")) {
			actual_samples = count;
			println($"选拔模式使用精确采样: {count} unknown cards → {actual_samples} samples");
		} else if (!config.performance_mode) {
			//根据未知卡牌数量动态调整
			var max_samples = Math.Min(count == 1 ? 5 : //单张未知卡牌，采样3-5张
				count <= 2 ? 6 : // 2张未知卡牌，每张采样4-6张
				count <= 3 ? 7 : // 3张未知卡牌，每张采样5-7张
				8, max_cards);
			//进一步根据游戏阶段调整
			var board_occupancy = _get_board_occupancy(board_state);
			if (board_occupancy > 0.6) // 后期游戏，减少采样
				max_samples = Math.Max(3, max_samples - 2);
			actual_samples = Math.Min(max_samples, max_cards);
			println($"Dynamic sampling: {count} unknown cards → {actual_samples} samples (performance_mode: {config.performance_mode}");
		} else {
			actual_samples = Math.Min(count * config.fallback_sample_multiplier, max_cards);
			println($"Standard sampling: {count} unknown cards → {actual_samples} samples");
		}
		//1. 基础过滤：排除已使用的卡牌（可选）
		if (config.aggressive_sampling)
			// 激进模式：允许重复使用（对手可能有相同卡牌）
			available_cards = [..all_cards];
		else {
			// 保守模式：排除已使用的卡牌
			available_cards = all_cards.Where(card => !used_cards.Contains(card.card_id)).ToList();
			if (available_cards.Count == 0)
				available_cards = [..all_cards];
		}

		// 2. 根据规则进行智能采样
		return _smart_sampling_by_rules(available_cards, actual_samples, rules,
			board_state, known_hand, owner, can_use);
	}

	//计算棋盘占用率
	private static float _get_board_occupancy(Board? board_state) {
		if (board_state == null) return 0;
		var occupied = 0;
		for (var r = 0; r < 3; r++)
		for (var c = 0; c < 3; c++)
			if (board_state.get_card(r, c) != null)
				occupied++;
		return occupied / 9f;
	}

	//根据规则进行智能采样
	private List<Card> _smart_sampling_by_rules(List<Card> available_cards, int max_samples, List<string> rules, Board board_state, List<Card> known_hand, string owner, bool can_use) {
		//选拔规则优先级最高（影响所有采样）
		if (rules.Contains("选拔"))
			return _sample_for_draft_rule(available_cards, max_samples, board_state,
				known_hand, owner, can_use, rules);
		//优先处理连携类规则（同数、加算）
		if (rules.Contains("同数"))
			return _sample_for_same_number_rule(available_cards, max_samples, board_state, known_hand, owner, can_use);
		if (rules.Contains("加算"))
			return _sample_for_addition_rule(available_cards, max_samples, board_state, known_hand, owner, can_use);
		if (rules.Contains("同类强化") || rules.Contains("同类弱化"))
			return _sample_for_same_type_rules(available_cards, max_samples, board_state, known_hand, owner, can_use);
		if (rules.Contains("逆转"))
			return _sample_for_reverse_rule(available_cards, max_samples, owner, can_use);
		if (rules.Contains("王牌杀手"))
			return _sample_for_ace_killer_rule(available_cards, max_samples, owner, can_use);
		return _sample_balanced_cards(available_cards, max_samples, owner, can_use);
	}

/*
 *   专门为对手生成未知卡牌，考虑真实玩家的策略行为
        现在支持动态采样数量调整

        Args:
            count: 需要生成的卡牌数量
            rules: 当前游戏规则
            used_cards: 已使用的卡牌ID集合
            board_state: 当前棋盘状态
            known_hand: 已知的手牌
            owner: 卡牌所有者
            can_use: 是否可用（秩序/混乱规则）
 */
	public List<Card> generate_opponent_cards(int count, List<string> rules, HashSet<int> used_cards, Board? board_state = null, List<Card>? known_hand = null, string? owner = null, bool can_use = true) {
		var config = UnknownCardConfig.get_sampling_config();
		var max_cards = UnknownCardConfig.get_max_cards_per_unknown();
		var board_occupancy = _get_board_occupancy(board_state);
		int actual_samples;
		if (config.performance_mode) {
			//对手卡牌采样更加保守
			var max_samples = Math.Min(count == 1 ? 4 : //单张对手卡牌采样4张
				count <= 2 ? 5 : //2张对手卡牌每张采样5张
				6, max_cards);
			if (board_occupancy >= 0.65f || count <= 2)
				max_samples = Math.Max(max_samples, Math.Min(12, max_cards));
			else if (board_occupancy >= 0.45)
				max_samples = Math.Max(max_samples, Math.Min(8, max_cards));
			actual_samples = Math.Min(max_samples, max_cards);
		} else {
			var base_multiplier = config.fallback_sample_multiplier;
			var sample_multiplier = base_multiplier;
			if (board_occupancy >= 0.65)
				sample_multiplier = Math.Max(base_multiplier, count == 1 ? 16 : 12);
			else if (board_occupancy >= 0.45 || count <= 2)
				sample_multiplier = Math.Max(base_multiplier, count <= 2 ? 12 : 8);
			actual_samples = Math.Min(count * sample_multiplier, max_cards);
		}
		println($"Opponent sampling: {count} unknown cards → {actual_samples} samples (occupied={board_occupancy:.0%})");
		//全部卡牌可用（对手可能有重复卡牌）
		List<Card> available_cards = [..all_cards];
		//基于规则的对手行为建模
		if (!config.advanced.opponent_behavior_modeling) {
			//如果未启用行为建模，使用简化的智能采样
			return _smart_sampling_by_rules(available_cards, actual_samples, rules,
				board_state, known_hand, owner, can_use);
		}
		// 分析当前游戏局势，选择最符合对手策略的采样方法
		var primary_rule = _determine_primary_rule(rules);
		if (primary_rule == "选拔")
			return _sample_strategic_draft_cards(available_cards, actual_samples, board_state,
				known_hand, owner, can_use, rules);
		if (primary_rule == "同数")
			return _sample_strategic_same_number_cards(available_cards, actual_samples, board_state,
				known_hand, owner, can_use, rules);
		if (primary_rule == "加算")
			return _sample_strategic_addition_cards(available_cards, actual_samples, board_state,
				known_hand, owner, can_use, rules);
		if (primary_rule == "同类强化" || primary_rule == "同类弱化")
			return _sample_strategic_same_type_cards(available_cards, actual_samples, board_state,
				known_hand, owner, can_use, rules);
		if (primary_rule == "逆转")
			return _sample_strategic_reverse_cards(available_cards, actual_samples, owner, can_use);
		if (primary_rule == "王牌杀手")
			return _sample_strategic_ace_killer_cards(available_cards, actual_samples, owner, can_use);
		return _sample_strategic_balanced_cards(available_cards, actual_samples, owner, can_use);
	}

	//确定主要规则，用于决定对手策略
	private static string _determine_primary_rule(List<string> rules) {
		//规则优先级（选拔规则优先级最高，连携类规则次之）
		Dictionary<string, int> rule_priority = new() {
			{ "选拔", 15 }, // 最高优先级（影响所有采样）
			{ "同数", 10 }, // 连携类规则优先级高
			{ "加算", 9 }, // 连携类规则优先级高
			{ "同类强化", 8 },
			{ "同类弱化", 7 },
			{ "逆转", 6 },
			{ "王牌杀手", 5 },
			{ "秩序", 3 },
			{ "混乱", 2 }
		};
		var applicable_rules = rules.Where(rule_priority.ContainsKey).Select(rule => (rule, rule_priority.GetValueOrDefault(rule, 1)));
		if (applicable_rules.Count() > 0)
			//返回优先级最高的规则
			return applicable_rules.MaxBy(x => x.Item2).rule;
		return "balanced"; // 默认平衡策略
	}

	//战略性同数卡牌采样（高级对手行为）
	private static List<Card> _sample_strategic_same_number_cards(List<Card> available_cards, int count, Board? board_state, List<Card>? known_hand, string owner, bool can_use, List<string> rules) {
		var config = UnknownCardConfig.get_sampling_config();
		var behavior_config = config.opponent_behavior.同数;
		// 更智能的同数策略分析
		List<Card> trap_setup_cards = [];
		List<Card> counter_cards = [];
		List<Card> defensive_cards = [];
		foreach (var card in available_cards) {
			int[] card_values = [card.up, card.right, card.down, card.left];
			// 检查是否适合设置连携陷阱
			if (_is_excellent_trap_card(card, board_state, behavior_config))
				trap_setup_cards.Add(card);
			else if (_is_counter_play_card(card, board_state, known_hand))
				counter_cards.Add(card);
			else if (_is_defensive_card(card, board_state))
				defensive_cards.Add(card);
		}
		List<Card> result = [];
		//60% 陷阱设置卡牌（对手更倾向于主动设置连携）
		var trap_count = int.Max(1, (int)(count * 0.6));
		if (trap_setup_cards.Count > 0)
			result.AddRange(_sample_cards_from_pool(trap_setup_cards,
				int.Min(trap_count, trap_setup_cards.Count),
				owner, can_use, true));
		//25% 反制卡牌
		var remaining = count - result.Count;
		var counter_count = remaining > 0 ? int.Max(1, (int)(remaining * 0.4)) : 0;
		if (counter_cards.Count > 0 && counter_count > 0)
			result.AddRange(_sample_cards_from_pool(counter_cards,
				int.Min(counter_count, counter_cards.Count),
				owner, can_use, true));
		//剩余用防御/随机卡牌补足
		remaining = count - result.Count;
		if (remaining > 0) {
			var all_remaining = available_cards.Where(card => result.All(r => card.card_id != r.card_id)).ToList();
			if (all_remaining.Count > 0)
				result.AddRange(_sample_cards_from_pool(all_remaining, remaining, owner, can_use, true));
		}
		return result.Take(count).ToList();
	}

	/*
	 *    """战略性选拔卡牌采样（对手行为建模）"""
        # 对手在选拔模式下的策略思考：
        # 1. 优先保留高星级卡牌到关键时刻
        # 2. 早期使用中低星级卡牌
        # 3. 根据剩余星级配额调整策略
	 */
	private List<Card> _sample_strategic_draft_cards(List<Card> available_cards, int count, Board? board_state, List<Card>? known_hand, string owner, bool can_use, List<string> rules) => _sample_for_draft_rule(available_cards, count, board_state,
		known_hand, owner, can_use, rules);

	/*
	 *    """战略性加算卡牌采样（高级对手行为）"""
         类似同数，但重点关注加算组合
	 */
	private static List<Card> _sample_strategic_addition_cards(List<Card> available_cards, int count, Board? board_state, List<Card>? known_hand, string owner, bool can_use, List<string> rules) => _sample_for_addition_rule(available_cards, count,
		board_state,
		known_hand, owner, can_use, true);

	//战略性同类卡牌采样（考虑雪球/破坏策略）
	private List<Card> _sample_strategic_same_type_cards(List<Card> available_cards, int count, Board? board_state, List<Card>? known_hand, string owner, bool can_use, List<string> rules) => _sample_for_same_type_rules(available_cards, count, board_state,
		known_hand, owner, can_use, true);

	//战略性逆转卡牌采样
	private static List<Card> _sample_strategic_reverse_cards(List<Card> available_cards, int count, string owner, bool can_use) => _enhanced_reverse_sampling(available_cards, count, owner, can_use);

	//战略性王牌杀手卡牌采样
	private static List<Card> _sample_strategic_ace_killer_cards(List<Card> available_cards, int count, string owner, bool can_use) {
		var config = UnknownCardConfig.get_sampling_config();
		var behavior_config = config.opponent_behavior.王牌杀手;
		List<Card> ace_killer_cards = [];
		List<Card> mid_value_cards = [];
		List<Card> other_cards = [];
		var ace_preference = behavior_config.ace_killer_preference;
		var mid_preference = behavior_config.mid_value_preference;
		foreach (var card in available_cards) {
			int[] values = [card.up, card.right, card.down, card.left];
			var has_ace_killer = Enumerable.Contains(values, 1) || Enumerable.Contains(values, 10);

			if (has_ace_killer)
				// 根据偏好系数重复添加
				for (var i = 0; i < MathF.Floor(ace_preference * 10); i++) {
					ace_killer_cards.Add(card);
				}
			else if (values.Any(val => val is >= 4 and <= 7))
				for (var i = 0; i < MathF.Floor(mid_preference * 10); i++) {
					mid_value_cards.Add(card);
				}
			else
				other_cards.Add(card);
		}
		//混合采样
		var all_weighted = ace_killer_cards.Concat(mid_value_cards).Concat(other_cards).ToList();
		if (all_weighted.Count >= count) {
			var sampled = all_weighted.Sample(count);
			//去重
			List<Card> unique_cards = [];
			HashSet<int> seen_ids = [];
			foreach (var card in sampled) {
				if (!seen_ids.Contains(card.card_id)) {
					unique_cards.Add(card);
					seen_ids.Add(card.card_id);
				}
				if (unique_cards.Count >= count)
					break;
			}
			return _sample_cards_from_pool(unique_cards.Take(count).ToList(), count, owner, can_use, true);
		}
		return _sample_cards_from_pool(available_cards.Take(count).ToList(), count, owner, can_use, true);
	}

	//战略性平衡卡牌采样（默认策略）
	private List<Card> _sample_strategic_balanced_cards(List<Card> available_cards, int count, string
		owner, bool can_use) => _sample_balanced_cards(available_cards, count, owner, can_use, true);

	//判断卡牌是否为优秀的陷阱设置卡牌
	//除了基础的同数判断，还考虑位置策略
	private static bool _is_excellent_trap_card(Card card, Board board_state, SamplingConfig.OpponentBehavior.C同数 behavior_config) {
		var is_basic_good = _is_good_for_same_number_combo(card, board_state, behavior_config);
		if (!is_basic_good)
			return false;
		//进一步检查：是否有多个相同数值（更容易触发连携）
		int[] card_values = [card.up, card.right, card.down, card.left];
		Dictionary<int, int> value_counts = [];
		foreach (var val in card_values)
			value_counts[val] = value_counts.GetValueOrDefault(val, 0) + 1;
		//有2个或以上相同数值的卡牌更适合设置陷阱
		var max_count = value_counts.Values.Max();
		return max_count >= 2;
	}

	/*
	 *判断卡牌是否适合反制对手策略
	 * 基于已知信息判断是否能有效反制
	 * 这里可以分析对手可能的下一步，选择相应的反制卡牌
	 */
	private static bool _is_counter_play_card(Card card, Board board_state, List<Card> known_hand) {
		int[] card_values = [card.up, card.right, card.down, card.left];
		var avg_value = card_values.Sum() / 4f;
		//中等偏高数值适合反制
		return 5.5 <= avg_value && avg_value <= 7.5;
	}

	//为同类强化/弱化规则采样卡牌
	private List<Card> _sample_for_same_type_rules(List<Card> available_cards, int count, Board board_state, List<Card> known_hand, string owner, bool can_use, bool is_opponent = false) {
		List<Card> result = [];
		//分析棋盘和已知手牌的类型分布
		var type_priority = _analyze_type_priority(board_state, known_hand);
		//30%概率选择优先类型，40%概率选择平衡分布，30%概率随机
		var priority_count = int.Max(1, (int)(count * 0.3));
		var balanced_count = int.Max(1, (int)(count * 0.4));
		var random_count = count - priority_count - balanced_count;
		//优先类型采样
		if (type_priority.Count > 0 && priority_count > 0) {
			var priority_type = type_priority[0];
			var type_cards = available_cards.Where(card => card_type_map[card.card_id] == priority_type).ToList();
			if (type_cards.Count > 0)
				result.AddRange(_sample_cards_from_pool(type_cards, priority_count, owner, can_use, is_opponent));
		}
		//平衡分布采样
		if (balanced_count > 0) {
			var balanced_cards = _get_balanced_type_sample(available_cards, balanced_count);
			result.AddRange(_sample_cards_from_pool(balanced_cards, balanced_count, owner, can_use, is_opponent));
		}
		//随机采样补足
		var remaining = count - result.Count;
		if (remaining > 0) {
			var remaining_cards = available_cards.Where(card => result.All(r => card.card_id != r.card_id)).ToList();
			if (remaining_cards.Count > 0)
				result.AddRange(_sample_cards_from_pool(remaining_cards, remaining, owner, can_use, is_opponent));
		}
		return result.Take(count).ToList();
	}

	//为同数规则采样卡牌（模拟玩家倾向于设置连携陷阱）
	private static List<Card> _sample_for_same_number_rule(List<Card> available_cards, int count, Board board_state, List<Card> known_hand, string owner, bool can_use, bool is_opponent = false) {
		var config = UnknownCardConfig.get_sampling_config();
		var behavior_config = config.opponent_behavior.同数;
		//分析棋盘状态，寻找可能的同数机会
		List<Card> combo_cards = [];
		List<Card> defensive_cards = [];
		foreach (var card in available_cards) {
			int[] card_values = [card.up, card.right, card.down, card.left];
			//检查是否适合设置同数陷阱
			if (_is_good_for_same_number_combo(card, board_state, behavior_config))
				combo_cards.Add(card);
			else if (_is_defensive_card(card, board_state))
				defensive_cards.Add(card);
		}
		List<Card> result = [];
		var combo_count = int.Max(1, (int)(count * behavior_config.combo_setup_ratio));
		var defensive_count = int.Max(1, (int)(count * behavior_config.defensive_ratio));
		var random_count = count - combo_count - defensive_count;
		//连携设置卡牌
		if (combo_cards.Count > 0 && combo_count > 0)
			result.AddRange(_sample_cards_from_pool(combo_cards, combo_count, owner, can_use, is_opponent));
		//防御性卡牌
		var remaining = count - result.Count;
		if (defensive_cards.Count > 0 && defensive_count > 0 && remaining > 0) {
			var actual_defensive = Math.Min(defensive_count, remaining);
			result.AddRange(_sample_cards_from_pool(defensive_cards, actual_defensive, owner, can_use, is_opponent));
		}
		//随机补足
		remaining = count - result.Count;
		if (remaining > 0) {
			var remaining_cards = available_cards.Where(card => result.All(r => card.card_id != r.card_id)).ToList();
			if (remaining_cards.Count > 0)
				result.AddRange(_sample_cards_from_pool(remaining_cards, remaining, owner, can_use, is_opponent));
		}
		return result.Take(count).ToList();
	}

	//为加算规则采样卡牌（模拟玩家倾向于设置加算连携）
	private static List<Card> _sample_for_addition_rule(List<Card> available_cards, int count, Board board_state, List<Card> known_hand, string owner, bool can_use, bool is_opponent = false) {
		var config = UnknownCardConfig.get_sampling_config();
		var behavior_config = config.opponent_behavior.加算;
		//分析可能的加算组合
		List<Card> sum_combo_cards = [];
		List<Card> defensive_cards = [];
		foreach (var card in available_cards) {
			if (_is_good_for_addition_combo(card, board_state, behavior_config))
				sum_combo_cards.Add(card);
			else if (_is_defensive_card(card, board_state))
				defensive_cards.Add(card);
		}
		List<Card> result = [];
		var combo_count = int.Max(1, (int)(count * behavior_config.sum_combo_ratio));
		var defensive_count = int.Max(1, (int)(count * behavior_config.defensive_ratio));
		//加算连携卡牌
		if (sum_combo_cards.Count > 0 && combo_count > 0)
			result.AddRange(_sample_cards_from_pool(sum_combo_cards, combo_count, owner, can_use, is_opponent));
		//防御性卡牌
		var remaining = count - result.Count;
		if (defensive_cards.Count > 0 && defensive_count > 0 && remaining > 0) {
			var actual_defensive = Math.Min(defensive_count, remaining);
			result.AddRange(_sample_cards_from_pool(defensive_cards, actual_defensive, owner, can_use, is_opponent));
		}
		//随机补足
		remaining = count - result.Count;
		if (remaining > 0) {
			var remaining_cards = available_cards.Where(card => result.All(r => card.card_id != r.card_id)).ToList();
			if (remaining_cards.Count > 0)
				result.AddRange(_sample_cards_from_pool(remaining_cards, remaining, owner, can_use, is_opponent));
		}
		return result.Take(count).ToList();
	}

	//为逆转规则采样卡牌（偏好低数值，考虑对手行为）
	//使用增强的逆转采样，考虑对手的真实行为模式
	private static List<Card> _sample_for_reverse_rule(List<Card> available_cards, int count, string owner, bool can_use) => _enhanced_reverse_sampling(available_cards, count, owner, can_use);

	//为王牌杀手规则采样卡牌（偏好1和A，考虑对手策略）
	//直接使用战略性王牌杀手采样
	private static List<Card> _sample_for_ace_killer_rule(List<Card> available_cards, int count, string owner, bool can_use) => _sample_strategic_ace_killer_cards(available_cards, count, owner, can_use);
	/*
	 为选拔规则进行智能采样 - 不限制采样广度，保持不对称博弈的预测能力

        Args:
            available_cards: 可用卡牌池
            count: 需要采样的数量
            board_state: 棋盘状态
            known_hand: 已知手牌
            owner: 卡牌所有者
            can_use: 是否可用
            rules: 所有规则列表
            */

	private List<Card> _sample_for_draft_rule(List<Card> available_cards, int count, Board board_state, List<Card> known_hand, string owner, bool can_use, List<string> rules) {
		var config = UnknownCardConfig.get_sampling_config();
		var draft_config = config.draft_mode;
		//分析当前星级使用情况（用于评估，但不强制限制）
		var star_usage = _analyze_star_usage(board_state, known_hand);
		var available_stars = _calculate_available_stars(star_usage, draft_config);
		println("选拔模式星级分析: 已使用: ");
		println(star_usage);
		println("可用: ");
		println(available_stars);
		// 在选拔模式下，不限制采样，而是提供更广泛的预测
		// 但是会根据星级约束给予不同的权重
		List<Card> weighted_candidates = [];
		foreach (var card in available_cards) {
			var star_level = card_star_map.GetValueOrDefault(card.card_id, 1);
			var base_weight = 1.0;
			// 根据星级约束调整权重，但不完全排除
			if (available_stars.GetValueOrDefault(star_level, 0) > 0)
				// 还有配额的星级，给予更高权重
				base_weight = 2.0;
			else if (available_stars.GetValueOrDefault(star_level, 0) == 0)
				// 配额已满的星级，给予较低权重但不排除
				base_weight = 0.3;
			//根据当前规则调整权重
			var rule_score = _calculate_card_rule_score(card, rules, board_state);
			//新增：边角战略评分
			var corner_score = _calculate_corner_strategy_score(card, board_state);
			var final_weight = base_weight * (1.0 + rule_score * 0.3 + corner_score * 0.4);
			// 添加到加权候选池中
			for (var i = 0; i < Math.Max(1, (int)Math.Round(final_weight * 10)); i++)
				weighted_candidates.Add(card);
		}
		//进行加权随机采样
		if (weighted_candidates.Count < count)
			// 如果加权候选不足，使用所有候选
			return _sample_cards_from_pool(available_cards, Math.Min(count, available_cards.Count),
				owner, can_use, true);
		//从加权池中采样
		var sampled = weighted_candidates.Sample(count);
		//去重
		List<Card> unique_cards = [];
		HashSet<int> seen_ids = [];
		foreach (var card in sampled) {
			if (!seen_ids.Contains(card.card_id)) {
				unique_cards.Add(card);
				seen_ids.Add(card.card_id);
			}
			if (unique_cards.Count >= count)
				break;
		}
		//如果去重后不足，补充更多卡牌
		if (unique_cards.Count < count) {
			var remaining_cards = available_cards.Where(card => !seen_ids.Contains(card.card_id)).ToList();
			var additional = Math.Min(count - unique_cards.Count, remaining_cards.Count);
			if (additional > 0)
				unique_cards.AddRange(remaining_cards.Sample(additional));
		}
		return _sample_cards_from_pool(unique_cards.Take(count).ToList(), count,
			owner, can_use, true);
	}

	//分析当前星级使用情况
	private Dictionary<int, int> _analyze_star_usage(Board? board_state, List<Card> known_hand) {
		Dictionary<int, int> star_usage = new() {
			{ 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 }, { 5, 0 }
		};
		//统计棋盘上的卡牌星级
		if (board_state != null) {
			for (var r = 0; r < 3; r++) {
				for (var c = 0; c < 3; c++) {
					var card = board_state.get_card(r, c);
					if (card != null && card.card_id != -1 && card_star_map.ContainsKey(card.card_id)) {
						var star = card_star_map.GetValueOrDefault(card.card_id, 1);
						star_usage[star] += 1;
					}
				}
			}
		}
		//统计已知手牌的星级（包括己方和对手的已知卡牌）
		if (known_hand.Count > 0) {
			foreach (var card in known_hand) {
				if (card.card_id != -1 && card_star_map.ContainsKey(card.card_id) &&
				    !(card is { up: 0, right: 0, down: 0, left: 0 })) {
					// 排除未知卡牌
					var star = card_star_map.GetValueOrDefault(card.card_id, 1);
					star_usage[star] += 1;
				}
			}
		}
		return star_usage;
	}

	//计算每个星级还能使用的数量
	private static Dictionary<int, int> _calculate_available_stars(Dictionary<int, int> star_usage, SamplingConfig.DraftMode draft_config) {
		var total_limits = draft_config.total_star_limits;
		Dictionary<int, int> available_stars = [];
		foreach (var p in total_limits) {
			var star = p.Key;
			var limit = p.Value;
			var used = star_usage.GetValueOrDefault(star, 0);
			var available = Math.Max(0, total_limits[star] - used);
			available_stars[star] = available;
		}
		return available_stars;
	}

	//智能星级分配策略
	private List<Card> _intelligent_star_distribution(List<Card> available_cards, int count, Dictionary<int, int> available_stars, string owner, bool can_use, List<string> rules, Board board_state) {
		var config = UnknownCardConfig.get_sampling_config();
		var draft_config = config.draft_mode;
		//按星级分组可用卡牌
		Dictionary<int, List<Card>> cards_by_star = [];
		foreach (var card in available_cards) {
			var star = card_star_map.GetValueOrDefault(card.card_id, 1);
			if (available_stars.GetValueOrDefault(star, 0) > 0) {
				// 只考虑还有配额的星级
				if (!cards_by_star.ContainsKey(star))
					cards_by_star[star] = [];
				cards_by_star[star].Add(card);
			}
		}
		if (cards_by_star.Count == 0) {
			println("没有符合星级限制的卡牌，使用回退策略");
			return _draft_fallback_sampling(available_cards, count, owner, can_use, rules);
		}
		List<Card> result = [];
		var remaining_count = count;
		//第一步：优先分配高价值星级（4星、5星）
		var priority_stars = draft_config.priority_stars;
		priority_stars.Sort();
		priority_stars = priority_stars.Reverse().ToArray();
		foreach (var star in priority_stars) {
			if (cards_by_star.ContainsKey(star) && available_stars.GetValueOrDefault(star, 0) > 0 && remaining_count > 0) {
				//计算这个星级应该分配多少张
				var star_allocation = Math.Min(Math.Min(
					remaining_count,
					available_stars[star]), Math.Min(
					cards_by_star[star].Count,
					Math.Max(1, remaining_count / 3) // 至少分配1张，最多分配1/3
				));
				//从该星级卡牌中智能选择
				var star_cards = _select_best_cards_for_rules(cards_by_star[star],
					star_allocation, rules, board_state);
				var selected = _sample_cards_from_pool(star_cards, star_allocation,
					owner, can_use, true);
				result.AddRange(selected);
				remaining_count -= selected.Count;
				available_stars[star] -= selected.Count;
				// 从cards_by_star中移除已选择的卡牌
				var selected_ids = new HashSet<int>(selected.Select(card => card.card_id));
				cards_by_star[star] = cards_by_star[star].Where(card => !selected_ids.Contains(card.card_id)).ToList();
			}
		}
		//第二步：分配中等星级（2星、3星）
		int[] mid_stars = [2, 3];
		foreach (var star in mid_stars) {
			if (cards_by_star.ContainsKey(star) && available_stars.GetValueOrDefault(star, 0) > 0 && remaining_count > 0) {
				var star_allocation = Math.Min(Math.Min(remaining_count, available_stars[star]), cards_by_star[star].Count);
				var star_cards = _select_best_cards_for_rules(cards_by_star[star],
					star_allocation, rules, board_state);
				var selected = _sample_cards_from_pool(star_cards, star_allocation,
					owner, can_use, true);
				result.AddRange(selected);
				remaining_count -= selected.Count;
				available_stars[star] -= selected.Count;
				// 从cards_by_star中移除已选择的卡牌
				var selected_ids = new HashSet<int>(selected.Select(card => card.card_id));
				cards_by_star[star] = cards_by_star[star].Where(card => !selected_ids.Contains(card.card_id)).ToList();
			}
		}
		//第三步：用低星级补足
		if (remaining_count > 0 && cards_by_star.ContainsKey(1) && available_stars.GetValueOrDefault(1, 0) > 0) {
			var star_allocation = Math.Min(Math.Min(remaining_count, available_stars[1]), cards_by_star[1].Count);
			var star_cards = cards_by_star[1];
			var selected = _sample_cards_from_pool(star_cards, star_allocation,
				owner, can_use, true);
			result.AddRange(selected);
			remaining_count -= selected.Count;
		}
		//如果还是不够，随机补足（理论上不应该发生）
		if (remaining_count > 0) {
			println($"选拔模式警告: 仍需补足{remaining_count}张卡牌");
			var fallback_cards = available_cards.Where(card => result.All(r => card.card_id != r.card_id)).ToList();
			if (fallback_cards.Count > 0) {
				var selected = _sample_cards_from_pool(fallback_cards, remaining_count,
					owner, can_use, true);
				result.AddRange(selected);
			}
		}
		println($"选拔模式完成: 生成{result.Count}张卡牌 (目标{count}张)");
		return result.Take(count).ToList();
	}

	//根据规则选择最适合的卡牌
	private static List<Card> _select_best_cards_for_rules(List<Card> star_cards, int count, List<string> rules, Board board_state) {
		if (star_cards.Count == 0) return [];
		//如果需要的数量大于等于可用数量，直接返回所有卡牌
		if (count >= star_cards.Count) return star_cards;
		//根据其他规则进行评分
		List<(Card, float)> scored_cards = [];
		foreach (var card in star_cards) {
			var score = _calculate_card_rule_score(card, rules, board_state);
			scored_cards.Add((card, score));
		}
		//按分数排序，选择最佳的卡牌
		scored_cards.Sort((a, b) => b.Item2.CompareTo(a.Item2));
		return scored_cards.Take(count).Select(card => card.Item1).ToList();
	}

	//计算卡牌在当前规则下的评分
	private static float _calculate_card_rule_score(Card card, List<string> rules, Board board_state) {
		var score = 0.0f;
		// 基础数值评分
		var avg_value = (card.up + card.right + card.down + card.left) / 4f;
		score += avg_value * 0.1f;
		//根据规则调整评分
		if (rules.Contains("同数")) {
			//有重复数值的卡牌加分
			int[] values = [card.up, card.right, card.down, card.left];
			if (new HashSet<int>(values).Count < 4) {
				score += 2.0f;
			}
		}
		if (rules.Contains("加算")) {
			// 有常见和数的卡牌加分
			int[] values = [card.up, card.right, card.down, card.left];
			for (var i = 0; i < values.Length; i++) {
				for (var j = 0; j < values.Length; j++) {
					if (i != j && values[i] + values[j] is
						    8 or 10 or 12)
						score += 1.5f;
				}
			}
		}
		if (rules.Contains("逆转")) {
			// 低数值卡牌加分
			if (avg_value <= 5)
				score += 3.0f;
			else if (avg_value >= 8)
				score -= 2.0f;
		}
		if (rules.Contains("王牌杀手")) {
			// 含1或A的卡牌加分
			int[] values = [card.up, card.right, card.down, card.left];
			var ace_count = values.Count(v => v is 1 or 10);
			score += ace_count * 2.0f;
		}
		if (rules.Contains("同类强化") && card.card_type != null) {
			// 有类型的卡牌加分
			score += 1.0f;
		}
		if (rules.Contains("同类弱化") && card.card_type != null) {
			// 多样化类型更有价值
			score += 0.5f;
		}
		return score;
	}

	/*
	 * 计算卡牌的边角放置战略评分
        高数值边应该优先占据角落位置，避免弱势边暴露
	 */
	internal static float _calculate_corner_strategy_score(Card card, Board board_state) {
		List<int> values = [card.up, card.right, card.down, card.left]; // U, R, D, L
		var score = 0.0f;
		//识别高数值边（8, 9, A/10）
		var high_values = values.Where(v => v >= 8).ToList();
		var medium_values = values.Where(v => v is >= 5 and <= 7).ToList();
		var low_values = values.Where(v => v <= 4).ToList();
		//基础战略评分
		if (high_values.Count() >= 3) {
			// 三边及以上高数值 - 非常适合角落放置
			score += 5.0f;

			// 检查是否有弱势边需要保护
			if (low_values.Count() >= 1)
				score += 2.0f; //有弱势边需要隐藏，更适合角落
		} else if (high_values.Count() >= 2) {
			// 双边高数值 - 适合边角放置
			score += 3.0f;

			// 检查高数值边的位置组合
			var high_positions = values.Select((_, index) => index).Where(index => values[index] >= 8).ToList();

			// 相邻高数值边特别适合角落（如右下角：右边+下边）
			if (_are_adjacent_sides(high_positions)) {
				score += 2.0f;
			}
		} else if (high_values.Count() == 1) // 单边高数值
			score += 1.0f;
		//AA组合特殊处理
		var ace_positions = values.Select((_, index) => index).Where(index => values[index] == 10).ToList(); // A = 10
		if (ace_positions.Count() >= 2) {
			score += 4.0f;
			// AA在相邻位置特别适合对应角落
			if (_are_adjacent_sides(ace_positions)) {
				score += 3.0f;
				// 检查具体的AA组合并评估最优位置
				if (_is_optimal_aa_combination(ace_positions, values))
					score += 5.0f;
			}
		}
		//分析棋盘状态，评估最优角落是否可用
		if (board_state != null) {
			var corner_bonus = _evaluate_corner_availability(card, board_state);
			score += corner_bonus;
		}
		//弱势边暴露惩罚
		var weakness_penalty = _calculate_weakness_exposure_penalty(values);
		score -= weakness_penalty;
		return score;
	}

	//检查边的位置是否相邻
	private static bool _are_adjacent_sides(List<int> positions) {
		if (positions.Count < 2) return false;
		//棋盘边的相邻关系: 0(上)-1(右), 1(右)-2(下), 2(下)-3(左), 3(左)-0(上)
		var adjacency = new Dictionary<int, List<int>> {
			[0] = [1, 3],
			[1] = [0, 2],
			[2] = [1, 3],
			[3] = [0, 2]
		};
		for (var i = 0; i < positions.Count; i++)
		for (var j = i + 1; j < positions.Count; j++)
			if (adjacency[positions[i]].Contains(positions[j]))
				return true;
		return false;
	}

	//检查是否是最优的AA组合，适合直接占据最佳角落
	private static bool _is_optimal_aa_combination(List<int> ace_positions, List<int> values) {
		if (ace_positions.Count < 2) return false;
		//右下角最优：右(1) + 下(2) = AA
		if (ace_positions.Contains(1) && ace_positions.Contains(2))
			return true;
		//左下角次优：左(3) + 下(2) = AA  
		if (ace_positions.Contains(3) && ace_positions.Contains(2))
			return true;
		//右上角：右(1) + 上(0) = AA
		if (ace_positions.Contains(1) && ace_positions.Contains(0))
			return true;
		//左上角：左(3) + 上(0) = AA
		if (ace_positions.Contains(3) && ace_positions.Contains(0))
			return true;
		return false;
	}

	//评估角落位置的可用性和适配度
	private static float _evaluate_corner_availability(Card card, Board board_state) {
		if (board_state == null) return 0.0f;
		List<(int, int)> available_corners = [];
		foreach (var pos in CORNER_SPAN) {
			if (board_state.get_card(pos.Item1, pos.Item2) == null) // 位置空闲
				available_corners.Add(pos);
		}
		if (available_corners.Count == 0)
			return 0.0f; // 没有可用角落
		var bonus = 0.0f;
		int[] values = [card.up, card.right, card.down, card.left];
		//评估每个可用角落的适配度
		foreach (var corner in available_corners) {
			var corner_score = _calculate_corner_fit_score(values, corner, board_state);
			bonus += corner_score * 0.5f; // 每个可用角落提供适配奖励
		}
		return MathF.Min(bonus, 3.0f); // 限制最大奖励
	}

	//计算卡牌对特定角落位置的适配度
	private static float _calculate_corner_fit_score(int[] values, (int, int) corner_pos, Board board_state) {
		var (row, col) = corner_pos;
		var fit_score = 0.0f;
		//检查相邻位置的威胁
		(int, int)[] adjacent_positions = [];
		if (row == 0 && col == 0) {
			// 左上角
			adjacent_positions = [(0, 1), (1, 0)]; // 右邻、下邻
			//卡牌的右边(1)和下边(2)数值重要
			if (values[1] >= 8) fit_score += 2.0f;
			if (values[2] >= 8) fit_score += 2.0f;
		} else if (row == 0 && col == 2) {
			// 右上角  
			adjacent_positions = [(0, 1), (1, 2)]; // 左邻、下邻
			//卡牌的左边(3)和下边(2)数值重要
			if (values[3] >= 8) fit_score += 2.0f;
			if (values[2] >= 8) fit_score += 2.0f;
		} else if (row == 2 && col == 0) {
			// 左下角
			adjacent_positions = [(1, 0), (2, 1)]; // 上邻、右邻
			//卡牌的上边(0)和右边(1)数值重要
			if (values[0] >= 8) fit_score += 2.0f;
			if (values[1] >= 8) fit_score += 2.0f;
		} else if (row == 2 && col == 2) {
			// 右下角
			adjacent_positions = [(1, 2), (2, 1)]; // 上邻、左邻
			//卡牌的上边(0)和左边(3)数值重要
			if (values[0] >= 8) fit_score += 2.0f;
			if (values[3] >= 8) fit_score += 2.0f;
		}
		return fit_score;
	}

	//计算弱势边暴露的惩罚
	private static float _calculate_weakness_exposure_penalty(List<int> values) {
		var penalty = 0.0f;
		//识别特别弱的边（1-3）
		var very_weak = values.Where(v => v <= 3).ToList();
		var weak = values.Where(v => v is >= 4 and <= 5).ToList();
		//弱势边越多，越需要小心放置
		penalty += very_weak.Count * 1.5f;
		penalty += weak.Count * 0.8f;
		//特殊情况：一张卡有极端差异（如1,9,9,9）
		var min_val = values.Min();
		var max_val = values.Max();
		if (max_val - min_val >= 7) // 数值差异很大
			penalty += 2.0f;
		return penalty;
	}

	//简单星级匹配策略
	private List<Card> _simple_star_matching(List<Card> available_cards, int count, Dictionary<int, int> available_stars, string owner, bool can_use) {
		List<Card> result = [];
		var remaining_count = count;
		//按星级从高到低分配
		foreach (var star in available_stars.Keys.ToList().OrderBy(i => -i)) {
			if (available_stars[star] > 0 && remaining_count > 0) {
				var star_cards = available_cards.Where(card =>
					card_star_map.GetValueOrDefault(card.card_id, 1) == star).ToList();
				if (star_cards.Count > 0) {
					var allocation = int.Min(int.Min(remaining_count, available_stars[star]), star_cards.Count);
					var selected = _sample_cards_from_pool(star_cards, allocation,
						owner, can_use, true);
					result.AddRange(selected);
					remaining_count -= selected.Count;
				}
			}
		}
		return result.Take(count).ToList();
	}

	//选拔模式的回退采样策略
	private List<Card> _draft_fallback_sampling(List<Card> available_cards, int count, string owner, bool can_use, List<string> rules) {
		println("使用选拔模式回退策略");
		//移除选拔规则，使用其他规则进行采样
		var fallback_rules = rules.Where(rule => rule != "选拔").ToList();
		if (fallback_rules.Count > 0) {
			//递归调用智能采样，但排除选拔规则
			return _smart_sampling_by_rules(available_cards, count, fallback_rules,
				null, [], owner, can_use);
		}
		//如果没有其他规则，使用平衡采样
		return _sample_balanced_cards(available_cards, count, owner, can_use);
	}

	//平衡采样（默认策略）
	//按星级分层采样
	private List<Card> _sample_balanced_cards(List<Card> available_cards, int count, string owner, bool can_use, bool is_opponent = false) {
		Dictionary<int, float> star_distribution = new() {
			{ 1, 0.4f },
			{ 2, 0.3f },
			{ 3, 0.2f },
			{ 4, 0.08f },
			{ 5, 0.02f }
		};
		List<Card> result = [];
		foreach (var (star, ratio) in star_distribution) {
			var star_count = int.Max(1, (int)MathF.Floor(count * ratio));
			var star_cards = available_cards.Where(card =>
				card_star_map.GetValueOrDefault(card.card_id, 1) == star).ToList();
			if (star_cards.Count > 0 && star_count > 0) {
				var sampled = _sample_cards_from_pool(star_cards, star_count, owner, can_use, is_opponent);
				result.AddRange(sampled);
				//从available_cards中移除已采样的卡牌
				var sampled_ids = sampled.Select(card => card.card_id).ToList();
				available_cards = available_cards.Where(card => !sampled_ids.Contains(card.card_id)).ToList();
			}
		}
		//补足到目标数量
		var remaining = count - result.Count;
		if (remaining > 0 && available_cards.Count > 0)
			result.AddRange(_sample_cards_from_pool(available_cards, remaining, owner, can_use, is_opponent));

		return result.Take(count).ToList();
	}

	//从卡牌池中采样指定数量的卡牌
	private static List<Card> _sample_cards_from_pool(List<Card> card_pool, int count, string owner, bool can_use, bool is_prediction = false, bool is_opponent = false) {
		if (card_pool == null || card_pool.Count == 0)
			return [];
		//确保不超过池子大小
		count = int.Min(count, card_pool.Count);
		//随机采样（不重复）
		var sampled_cards = card_pool.Sample(count);
		//创建新的Card实例，设置正确的owner和can_use
		List<Card> result = [];
		for (var i = 0; i < sampled_cards.Count; i++) {
			var card = sampled_cards[i];
			//对于对手预测卡牌，使用特殊ID标记（>= 1000）
			var card_id = card.card_id;
			if (is_opponent || is_prediction)
				card_id = 1000 + card.card_id; // 保持原始ID的映射关系
			var new_card = new Card(
				card.up,
				card.right,
				card.down,
				card.left,
				owner = owner,
				card_id = card_id) {
				card_type = card.card_type,
				can_use = can_use
			};
			//标记为生成的卡牌（用于区分真实已知和AI预测）
			if (is_opponent || is_prediction) {
				new_card._is_generated = true;
				new_card._is_prediction = true;
			}
			result.Add(new_card);
		}
		return result;
	}

	//分析类型优先级
	private static List<string> _analyze_type_priority(Board board_state, List<Card> known_hand) {
		Dictionary<string, int> type_counts = [];
		//分析棋盘上的类型
		if (board_state != null) {
			for (var r = 0; r < 3; r++)
			for (var c = 0; c < 3; c++) {
				var card = board_state.get_card(r, c);
				if (card is { card_type: not null }) {
					type_counts.TryAdd(card.card_type, 0);
					type_counts[card.card_type] += 2; //棋盘上的卡牌权重更高}
				}
			}
		}
		//分析已知手牌的类型
		if (known_hand.Count > 0) {
			foreach (var card in known_hand) {
				if (card.card_type != null) {
					type_counts.TryAdd(card.card_type, 0);
					type_counts[card.card_type] += 1;
				}
			}
		} //按出现频率排序
		return type_counts.OrderByDescending(x => x.Value).Select(x => x.Key).ToList();
	}

	//获取类型平衡的卡牌样本
	private List<Card> _get_balanced_type_sample(List<Card> available_cards, int count) {
		Dictionary<string, List<Card>> type_groups = [];
		foreach (var card in available_cards) {
			var card_type = card_type_map.GetValueOrDefault(card.card_id, "no_type");
			type_groups.TryAdd(card_type, []);
			type_groups[card_type].Add(card);
		}
		List<Card> result = [];
		var types = type_groups.Keys.ToList();
		//轮流从每个类型中选择卡牌
		for (var i = 0; i < count; i++) {
			var type_name = types[i % types.Count];
			if (type_groups.ContainsKey(type_name) && type_groups[type_name].Count > 0) {
				var card = type_groups[type_name].Sample(1).First();
				result.Add(card);
				type_groups[type_name].Remove(card);
			}
		}
		return result;
	}

	//判断卡牌是否适合同数连携
	private static bool _is_good_for_same_number_combo(Card card, Board board_state, SamplingConfig.OpponentBehavior.C同数 behavior_config) {
		int[] card_values = [card.up, card.right, card.down, card.left];
		//偏好中等数值，容易形成同数
		var preferred_values = behavior_config.preferred_values;
		;
		//检查是否有偏好数值
		var has_preferred = preferred_values.Any(val => Enumerable.Contains(card_values, val));
		//检查是否有重复数值（利于同数）
		var has_duplicates = new HashSet<int>(card_values).Count < 4;
		//避免极端数值
		var avoid_extreme = behavior_config.avoid_extreme_values;
		if (avoid_extreme) {
			var has_extreme = Enumerable.Contains(card_values, 1) || Enumerable.Contains(card_values, 10);
			if (has_extreme)
				return false;
		}
		return has_preferred || has_duplicates;
	}

	//判断卡牌是否适合加算连携
	private static bool _is_good_for_addition_combo(Card card, Board board_state, SamplingConfig.OpponentBehavior.C加算 behavior_config) {
		int[] card_values = [card.up, card.right, card.down, card.left];
		//检查是否有利于形成特定和数的组合
		var preferred_ranges = behavior_config.preferred_sum_ranges;
		//计算可能的和数
		List<int> possible_sums = [];
		for (var i = 0; i < card_values.Length; i++) {
			var val1 = card_values[i];
			for (var j = 0; j < card_values.Length; j++) {
				var val2 = card_values[j];
				if (i != j)
					possible_sums.Add(val1 + val2);
			}
		}
		//检查是否在偏好范围内
		foreach (var sum_val in possible_sums) {
			foreach (var (min_range, max_range) in preferred_ranges) {
				if (min_range <= sum_val && sum_val <= max_range)
					return true;
			}
		}
		//检查是否有互补数值（如3+7=10, 4+6=10等）
		var complementary = behavior_config.complementary_values;
		if (complementary) {
			int[] common_sums = [8, 10, 12]; // 常见的目标和数
			foreach (var target_sum in common_sums)
			foreach (var val in card_values) {
				var complement = target_sum - val;
				if (Enumerable.Contains(card_values, complement) && complement != val)
					return true;
			}
		}
		return false;
	}

	//判断卡牌是否适合防御策略
	//高数值卡牌通常更适合防御
	private static bool _is_defensive_card(Card card, Board board_state) {
		int[] card_values = [card.up, card.right, card.down, card.left];
		var avg_value = card_values.Sum() / 4f;

		// 平均值较高的卡牌更适合防御
		return avg_value >= 6.0f;
	}

	//增强的逆转规则采样，考虑对手行为
	private static List<Card> _enhanced_reverse_sampling(List<Card> available_cards, int count, string owner, bool can_use) {
		var config = UnknownCardConfig.get_sampling_config();
		var behavior_config = config.opponent_behavior.逆转;
		//按照对手偏好进行权重计算
		var low_preference = behavior_config.low_value_preference;
		var max_preferred = behavior_config.max_preferred_value;
		var high_avoidance = behavior_config.high_value_avoidance;
		List<Card> weighted_cards = [];
		foreach (var card in available_cards) {
			int[] card_values = [card.up, card.right, card.down, card.left];
			var avg_value = card_values.Sum() / 4f;
			// 计算权重
			var weight = 1.0f;
			if (avg_value <= max_preferred)
				weight = low_preference;
			else if (avg_value >= 8)
				weight = high_avoidance;
			for (var i = 0; i < (int)(weight * 10); i++)
				weighted_cards.Add(card); //权重转换为重复次数
		}
		//从加权列表中采样
		if (weighted_cards.Count >= count) {
			var sampled = weighted_cards.Sample(count);
			//去重
			List<Card> unique_cards = [];
			HashSet<int> seen_ids = [];
			foreach (var card in sampled) {
				if (!seen_ids.Contains(card.card_id)) {
					unique_cards.Add(card);
					seen_ids.Add(card.card_id);
				}
				if (unique_cards.Count >= count)
					break;
			}
			return _sample_cards_from_pool(unique_cards.Take(count).ToList(), count, owner, can_use, true);
		}
		return _sample_cards_from_pool(available_cards.Take(count).ToList(), count, owner, can_use, true);
	}

	//全局处理器实例
	private static UnknownCardHandler? _unknown_card_handler;

	//获取全局未知卡牌处理器
	public static UnknownCardHandler? get_unknown_card_handler() => _unknown_card_handler;

	//初始化全局未知卡牌处理器
	internal static void initialize_unknown_card_handler(List<Card> all_cards, Dictionary<int, string?> card_type_map, Dictionary<int, int> card_star_map) {
		_unknown_card_handler = new UnknownCardHandler(all_cards, card_type_map, card_star_map);
	}
}
using System.Collections.Generic;

namespace TtcServer.config;

public class UnknownCardConfig {
	public class SamplingConfig {
		//基础配置 - 大幅减少采样数量
		public int max_unknown_cards_per_hand = 150; // 每个未知手牌最多生成的卡牌数量
		public int fallback_sample_multiplier = 5; //回退策略的采样倍数
		//性能优化配置
		public bool performance_mode = false; //启用性能模式
		public bool aggressive_sampling = false; //激进采样模式
		public int min_samples_per_unknown = 5; //每个未知卡牌的最小采样数
		public int max_samples_per_unknown = 10; //每个未知卡牌的最大采样数

		public class RuleSpecific {
			public class C同类强化 {
				public float priority_ratio = 0.4f; //优先类型的比例
				public float balanced_ratio = 0.4f; //平衡分布的比例
				public float random_ratio = 0.2f; //随机采样的比例
			}

			public class C同类弱化 {
				public float priority_ratio = 0.3f; //同类弱化时降低优先类型比例
				public float balanced_ratio = 0.5f;
				public float random_ratio = 0.2f;
			}

			public class C逆转 {
				public float low_value_ratio = 0.7f; //低数值卡牌的比例
				public float random_ratio = 0.3f; //随机卡牌的比例
			}

			public class C王牌杀手 {
				public float special_ratio = 0.6f; //含1或A卡牌的比例
				public float normal_ratio = 0.4f; //普通卡牌的比例
			}

			public class C同数 {
				public float combo_setup_ratio = 0.5f; //设置连携陷阱的卡牌比例
				public float defensive_ratio = 0.3f; //防御性卡牌比例
				public float random_ratio = 0.2f; //随机卡牌比例
			}

			public class C加算 {
				public float sum_combo_ratio = 0.5f; //加算连携卡牌比例
				public float defensive_ratio = 0.3f; //防御性卡牌比例
				public float random_ratio = 0.2f; //随机卡牌比例
			}

			public class C选拔 {
				public bool exact_star_matching = true; //精确星级匹配
				public bool fallback_to_closest = true; //回退到最接近的星级
				public bool strategic_star_selection = true; //战略性星级选择
				public float confidence_boost = 0.3f; //选拔规则的置信度提升
				public int max_deviation = 0; //允许的最大星级偏差
			}

			public C同类强化 同类强化 = new();
			public C同类弱化 同类弱化 = new();
			public C逆转 逆转 = new();
			public C王牌杀手 王牌杀手 = new();
			public C同数 同数 = new();
			public C加算 加算 = new();
			public C选拔 选拔 = new();
		}

		public RuleSpecific rule_specific = new(); //规则特定配置

		public class StarDistribution {
			public float star_1 = 0.35f; //1星卡牌35 %
			public float star_2 = 0.30f; //2星卡牌30 %
			public float star_3 = 0.20f; //3星卡牌20 %
			public float star_4 = 0.10f; //4星卡牌10 %
			public float star_5 = 0.05f; //5星卡牌5 %
		}

		public StarDistribution star_distribution = new(); //星级分布配置（默认策略）

		public class TypeWeights {
			public float 兽人 = 1.0f;
			public float 拂晓 = 1.0f;
			public float 帝国 = 1.0f;
			public float 蛮神 = 1.0f;
			public float no_type = 0.8f; //无类型卡牌权重稍低
		}

		public TypeWeights type_weights = new();

		public class Advanced {
			public bool consider_board_synergy = true; // 是否考虑与棋盘的协同
			public bool consider_hand_synergy = true; // 是否考虑与已知手牌的协同
			public bool adaptive_sampling = true; // 是否使用自适应采样
			public bool position_aware = true; // 是否考虑位置相关性
			public bool opponent_behavior_modeling = true; // 是否启用对手行为建模
		}

		public Advanced advanced = new(); //高级配置

		public class OpponentBehavior {
			public class C同数 {
				public int[] preferred_values = [2, 3, 4, 5, 6]; // 容易形成同数的中等数值
				public bool avoid_extreme_values = true; // 避免极端数值(1, 10)
				public bool setup_traps = true; // 倾向于设置连携陷阱
				public float corner_preference = 1.2f; // 角落位置偏好系数
				public float combo_setup_ratio = 0.5f;
				public float defensive_ratio = 0.3f;
			}

			public class C加算 {
				public (int, int)[] preferred_sum_ranges = [(5, 9), (10, 14)]; // 偏好的加算范围
				public bool complementary_values = true; // 倾向于互补数值
				public bool setup_traps = true; // 倾向于设置连携陷阱
				public float edge_preference = 1.3f; // 边缘位置偏好系数
				public float sum_combo_ratio = 0.5f;
				public float defensive_ratio = 0.3f;
			}

			public class C逆转 {
				public float low_value_preference = 2.0f; // 强烈偏好低数值
				public float max_preferred_value = 5.0f; // 最大偏好数值
				public float high_value_avoidance = 0.3f; // 避免高数值的程度
			}

			public class C王牌杀手 {
				public float ace_killer_preference = 1.8f; // 对1和A的偏好系数
				public float mid_value_preference = 0.7f; // 对中等数值的偏好
				public bool strategic_positioning = true; // 战略性定位
			}

			public class C同类强化 {
				public float type_synergy_preference = 2.0f; // 类型协同偏好
				public bool snowball_strategy = true; // 雪球战略
				public bool defensive_typing = false; // 防御性类型选择
			}

			public class C同类弱化 {
				public float type_diversity_preference = 1.5f; // 类型多样性偏好
				public bool disruption_strategy = true; // 破坏战略
				public bool min_value_avoidance = true; // 避免最小值
			}

			public class C选拔 {
				public bool star_conservation_strategy = true; //星级保守策略
				public int[] early_game_stars = [1, 2, 3]; // 早期偏好使用的星级
				public int[] late_game_stars = [4, 5]; // 后期保留的星级
				public int adaptive_threshold = 5; // 棋盘卡牌数适应阈值
				public float high_star_preservation = 0.7f; // 高星级保留倾向
				public bool strategic_timing = true; // 战略性时机选择
			}

			public C同数 同数 = new(); //同数规则下的对手倾向
			public C加算 加算 = new(); //加算规则下的对手倾向
			public C逆转 逆转 = new(); //逆转规则下的对手倾向
			public C王牌杀手 王牌杀手 = new(); //逆转规则下的对手倾向
			public C同类强化 同类强化 = new(); //同类强化规则下的对手倾向
			public C同类弱化 同类弱化 = new(); //同类弱化规则下的对手倾向
			public C选拔 选拔 = new(); //拔规则下的对手倾向
		}

		public OpponentBehavior opponent_behavior = new(); //对手行为建模配置

		public class DraftMode {
			public Dictionary<int, int> total_star_limits = new() {
				{ 1, 2 }, //1星卡牌2张
				{ 2, 2 }, //2星卡牌2张
				{ 3, 2 }, //3星卡牌2张
				{ 4, 2 }, //4星卡牌2张
				{ 5, 2 } //5星卡牌2张
			};
			public bool strict_enforcement = true; //严格执行星级限制
			public bool intelligent_distribution = true; //智能分配剩余星级
			public bool opponent_modeling = true; //对手选拔建模
			public int[] priority_stars = [3, 4, 5]; // 优先考虑的星级（高价值卡牌）
			public int[] early_game_preference = [1, 2]; // 前期偏好的星级
			public int[] late_game_preference = [4, 5]; // 后期偏好的星级
		}

		public DraftMode draft_mode = new(); //选拔规则专用配置
	}

	// public class DebugConfig {
	// 	public  bool verbose_sampling = false;
	// 	public  bool log_statistics = false;
	// 	public  bool performance_monitoring = true;
	// }

	public SamplingConfig SAMPLING_CONFIG = new();
	// public  DebugConfig DEBUG_CONFIG = new();

	//快速访问函数
	//	获取采样配置
	public SamplingConfig get_sampling_config() => SAMPLING_CONFIG;

	//获取调试配置
	// public  DebugConfig get_debug_config() => DEBUG_CONFIG;

	//获取特定规则的配置
	// public  RuleSpecific get_rule_config(string rule_name) => SAMPLING_CONFIG.rule_specific;

	//获取星级分布
	// public  StarDistribution get_star_distribution() => SAMPLING_CONFIG.star_distribution;

	//获取每个未知手牌的最大生成数量
	public int get_max_cards_per_unknown() => SAMPLING_CONFIG.max_unknown_cards_per_hand;
}
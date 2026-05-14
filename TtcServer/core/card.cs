using System;
using System.Collections.Generic;

namespace TtcServer.core;

public class Card(int up, int right, int down, int left, string? owner = null, int? cardId = null, string? cardType = null, bool canUse = true) : ICloneable {
	// 原始数值（永远不变）
	public int base_up = up;
	public int base_down = down;
	public int base_right = right;
	public int base_left = left;
	//当前有效数值（用于显示和兼容性）
	public int up = up;
	public int right = right;
	public int down = down;
	public int left = left;
	public string? owner = owner; //归属方（'red'/'blue'/None）
	public int card_id = cardId ?? -1; //卡牌ID或类型编号
	public string? card_type = cardType; //卡牌类型（兽人、拂晓、帝国、蛮神）
	public bool can_use = canUse; //是否可以使用（用于秩序/混乱规则）
	//同类强化/弱化修正值
	public int type_modifier;
	public int? star;
	public bool _is_generated;
	public bool _is_prediction;

	public override string ToString() => $"Card(ID={card_id}, U={up}, R={right}, D={down}, L={left}, owner={owner}, type={card_type})";

	/*
	返回卡牌四面数值的字符串表示，格式如：U5 L2 D3 R1
	如果有同类修正，显示为：U5+1 L2+1 D3+1 R1+1
	*/
	public string display() {
		if (type_modifier != 0) {
			var modifier_str = $"{type_modifier:+d}";
			return $"U{base_up}{modifier_str} L{base_left}{modifier_str} D{base_down}{modifier_str} R{base_right}{modifier_str}";
		}
		return $"U{base_up} L{base_left} D{base_down} R{base_right}";
	}

	/*
	 返回4行字符串列表，不加颜色，每行宽度7。
	如果有同类修正，显示修正后的数值。
	*/
	public string[] display_multiline() {
		var owner_ = owner == null ? " " : owner.ToUpper()[0].ToString();
		var cid = card_id == null ? " " : card_id.ToString();
		var up_display = get_display_value("up");
		var right_display = get_display_value("right");
		var down_display = get_display_value("down");
		var left_display = get_display_value("left");
		var lines = new[] {
			$"[{owner_}:{cid}]",
			$"U{up_display}",
			$"L{left_display}   R{right_display}",
			$"D{down_display}"
		};
		return lines;
	}

	//获取指定方向的原始数值
	public int get_base_value(string direction) => direction switch {
		"up" => base_up,
		"right" => base_right,
		"down" => base_down,
		"left" => base_left,
		_ => -1
	};

	//获取应用同类修正后的数值
	public int get_modified_value(string direction) {
		var base_value = get_base_value(direction);
		return Math.Max(1, Math.Min(10, base_value + type_modifier));
	}

	//获取用于显示的数值字符串
	public string get_display_value(string direction) {
		if (type_modifier != 0) {
			var base_val = get_base_value(direction);
			var modifier_str = $"{type_modifier:+d}";
			return $"{base_val}{modifier_str}";
		}
		return get_base_value(direction).ToString();
	}

/*
根据规则获取指定方向的最终有效数值
考虑同类强化/弱化，但不包括王牌杀手规则（王牌杀手需要特殊处理）
 */
	public int get_effective_value(string direction, List<string> rules) => get_modified_value(direction); //应用同类修正

	//modified
/*
 比较两张卡牌的数值
		返回: 1表示我方胜, -1表示对方胜, 0表示平局
 */
	public int compare_values(string my_direction, Card other_card, string other_direction, List<string> rules) {
		var my_value = get_effective_value(my_direction, rules);
		var other_value = other_card.get_effective_value(other_direction, rules);
		//王牌杀手规则：只对 1 与 A(10) 之间的互相比较做特殊处理
		if (rules.Contains("王牌杀手")) {
			var my_is_ace_killer = my_value is 1 or 10;
			var other_is_ace_killer = other_value is 1 or 10;
			if (my_is_ace_killer && other_is_ace_killer) {
				if (my_value == other_value) return 0;
				if (my_value == 1 && other_value == 10) return rules.Contains("逆转") ? -1 : 1;
				if (my_value == 10 && other_value == 1) return rules.Contains("逆转") ? 1 : -1;
				return 0;
			}
		}

		if (rules.Contains("逆转")) {
			//逆转规则：数字小的一方获胜
			if (my_value < other_value) return 1;
			if (my_value > other_value) return -1;
		} else {
			//正常规则：数字大的一方获胜
			if (my_value > other_value) return 1;
			if (my_value < other_value) return -1;
		}
		return 0;
	}

	//	应用同类强化/弱化修正值
	public void apply_type_modifier(int modifier) {
		type_modifier = modifier;
		//更新显示用的当前数值（为了兼容性）
		up = get_modified_value("up");
		right = get_modified_value("right");
		down = get_modified_value("down");
		left = get_modified_value("left");
	}

/*
 * 修改卡牌的四个方向数值（用于同类强化/弱化）
 * 这是为了向后兼容而保留的方法
 */
	public void modify_stats(int delta) {
		apply_type_modifier(delta);
	}

//创建卡牌的深拷贝
	public object Clone() => new Card(base_up, base_right, base_down, base_left, owner, card_id, card_type, can_use) {
		type_modifier = type_modifier,
		up = get_modified_value("up"),
		right = get_modified_value("right"),
		down = get_modified_value("down"),
		left = get_modified_value("left")
	};
}
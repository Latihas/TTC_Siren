using System.Collections.Generic;
using System.Linq;

namespace TtcServer.core;

//幻卡玩家类，包含手牌（剩余卡牌）、已用卡牌。
public class Player(string name, List<Card> hand) {
	public readonly string name = name; //玩家名称
	public readonly List<Card> hand = hand; //剩余手牌（List[Card]）
	private readonly List<Card> used_cards = []; //已用卡牌

	//打出一张卡牌，移出手牌，加入已用卡牌。
	public void play_card(Card card) {
		if (hand.Contains(card)) {
			hand.Remove(card);
			used_cards.Add(card);
		}
	}

/*
 *   获取当前可以使用的卡牌列表
 *	 对于秩序/混乱规则，只返回can_use为True的卡牌
 */
	public List<Card> get_playable_cards(List<string> rules) {
		if (rules.Count > 0 && (rules.Contains("秩序") || rules.Contains("混乱"))) {
			return hand.Where(card => card.can_use).ToList();
		}
		return hand;
	}

	public override string ToString() => $"Player({name}, hand={hand}, used={used_cards})";
}
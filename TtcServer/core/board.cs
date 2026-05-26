using System;
using System.Collections.Generic;
using System.Linq;

namespace TtcServer.core;

public class Board : ICloneable {
	public Card?[][] grid = [
		[null, null, null],
		[null, null, null],
		[null, null, null]
	];

	//在指定位置放置卡牌，若成功返回True，若已被占用返回False
	public bool place_card(int row, int col, Card card) {
		if (grid[row][col] is null) {
			grid[row][col] = card;
			return true;
		}
		return false;
	}

	//获取指定格子的卡牌
	public Card? get_card(int row, int col) => grid[row][col];

	//判断指定格子是否为空。
	public bool is_empty(int row, int col) => grid[row][col] is null;

	//移除指定位置的卡牌，返回被移除的卡牌，如果位置为空则返回None。
	public Card? remove_card(int row, int col) {
		var card = grid[row][col];
		grid[row][col] = null;
		return card;
	}

	//返回所有可用（空）格子的坐标列表。
	public List<(int, int)> available_positions() {
		var available = new List<(int, int)>();
		for (var r = 0; r < 3; r++)
		for (var c = 0; c < 3; c++)
			if (grid[r][c] is null)
				available.Add((r, c));
		return available;
	}

	public bool has_card(int card_id) {
		return grid.SelectMany(row => row).OfType<Card>().Any(c => c.card_id == card_id);
	}

	//将棋盘状态转换为字符串。
	public override string ToString() {
		var card_lines = new string[]?[][] {
			[null, null, null],
			[null, null, null],
			[null, null, null]
		};
		for (var r = 0; r < 3; r++)
		for (var c = 0; c < 3; c++) {
			var card = grid[r][c];
			if (card is null)
				card_lines[r][c] = ["       ", "       ", "       ", "       "];
			else {
				//只在这里加颜色
				// var color_start = "";
				// var color_end = "";
				// if (card.owner == "red") {
				// 	color_start = "\033[31m";
				// 	color_end = "\033[0m";
				// } else if (card.owner == "blue") {
				// 	color_start = "\033[34m";
				// 	color_end = "\033[0m";
				// }
				card_lines[r][c] = card.display_multiline()
					// .Select(line => $"{color_start}{line}{color_end}")
					.ToArray();
			}
		}
		//拼接每一行
		List<string> lines = [];
		const string sep = "+-------+-------+-------+";
		for (var row = 0; row < 3; row++) {
			lines.Add(sep);
			for (var subline = 0; subline < 4; subline++) {
				var row1 = row;
				var subline1 = subline;
				lines.Add($"|{string.Join("|",
					Enumerable.Range(0, 3)
						.Select(col => card_lines[row1][col]?[subline1])
				)}|");
			}
		}
		lines.Add(sep);
		return string.Join("\n", lines);
	}

	public object Clone() {
		return new Board {
			grid = grid.Select(row => row.Select(c => c?.Clone() as Card).ToArray()).ToArray()
		};
	}
}
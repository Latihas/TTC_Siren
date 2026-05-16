using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using TtcServer.config;
using static TtcServer.AiServer;

namespace TtcServer;

public static class Utils {
	extension<T>(IEnumerable<T> source) {
		public List<T> Sample(int n) {
			var list = source.ToList();
			var sampleCount = Math.Min(n, list.Count);
			for (var i = 0; i < sampleCount; i++) {
				var randomIndex = Random.Shared.Next(i, list.Count);
				(list[i], list[randomIndex]) = (list[randomIndex], list[i]);
			}
			return list.Take(sampleCount).ToList();
		}

		public List<List<T>> GeneratePermutations(int length) {
			List<List<T>> result = [];
			var sourceList = source.ToList();
			if (length == 0) {
				result.Add([]);
				return result;
			}
			PermuteRecursive([], new bool[sourceList.Count]);
			return result;

			void PermuteRecursive(List<T> current, bool[] used) {
				if (current.Count == length) {
					result.Add([..current]);
					return;
				}
				for (var i = 0; i < sourceList.Count; i++) {
					if (used[i]) continue;
					used[i] = true;
					current.Add(sourceList[i]);
					PermuteRecursive(current, used);
					current.RemoveAt(current.Count - 1);
					used[i] = false;
				}
			}
		}
	}

	public static readonly (int, int)[] CORNER_SPAN = [(0, 0), (0, 2), (2, 0), (2, 2)];
	public static readonly (int, int)[] EDGE_SPAN = [(0, 1), (1, 0), (1, 2), (2, 1)];

	[SuppressMessage("ReSharper", "PropertyCanBeMadeInitOnly.Global")] 
	[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
	public class CardDatabaseEntry {
		public int Id { get; set; }
		public string Name { get; set; }
		public int Top { get; set; }
		public int Bottom { get; set; }
		public int Left { get; set; }
		public int Right { get; set; }
		public int Star { get; set; }
		public string? TripleTriadCardType { get; set; }
		public int SortKey { get; set; }
		public int Order { get; set; }
		public int UIPriority { get; set; }
		public uint AcquisitionType { get; set; }
	}

	public class AiMoveResponse {
		public string card { get; set; }
		public int card_id { get; set; }
		public int[] pos { get; set; }
		public OpponentHandAnalysis opponent_hand_analysis { get; set; }
		public WinProbability win_probability { get; set; }
		public Recommendation recommendation { get; set; }

		public class PerformanceStats {
			public int nodes_searched { get; set; }
			public int search_depth { get; set; }
			public double tt_hit_rate { get; set; }
			public double cutoff_rate { get; set; }
			public int unknown_cards_processed { get; set; }
			public bool performance_optimizations_active { get; set; }
		}

		public PerformanceStats performance_stats { get; set; }
		public List<SearchProgressData> search_progress { get; set; }
		public SearchSummary? search_summary { get; set; }
	}

	public class OpponentHandAnalysis {
		public List<PredictedCard> predicted_cards { get; set; }
		public int total_unknown { get; set; }
		public string strategy_analysis { get; set; }
		public string draft_analysis { get; set; }
	}

	public class PredictedCard {
		public string card { get; set; }
		public object star { get; set; } // 可能是数字或字符串
		public double confidence { get; set; }
		public string reasoning { get; set; }
		public bool is_predicted { get; set; }
	}

	public class WinProbability {
		public double current { get; set; }
		public double after_move { get; set; }
		public double confidence { get; set; }
	}

	public class Recommendation {
		public string move_reasoning { get; set; }
		public string strategic_value { get; set; }
		public List<AlternativeMove> alternative_moves { get; set; }
	}

	public class AlternativeMove {
		public string card { get; set; }
		public int[] pos { get; set; }
		public double value { get; set; }
	}

	private static IPluginLog Log;
	internal static IDataManager DataManager;
	public static UnknownCardConfig UnknownCardConfig;

	public static void Init(IPluginLog log, IDataManager dataManager, UnknownCardConfig unknownCardConfig) {
		Log = log;
		DataManager = dataManager;
		UnknownCardConfig = unknownCardConfig;
	}

	private static readonly StringBuilder sb = new();

	internal static void print(string str) => sb.Append(str);

	internal static void println(string str = "") {
		sb.Append(str);
		Log.Debug(sb.ToString());
		sb.Clear();
	}
}
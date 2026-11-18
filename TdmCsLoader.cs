// TdmCS: Team Deathmatch bergaya Counter-Strike untuk Survivalcraft, memakai mod senjata CS (ScCsgoKnives + ScCsgoTactical).
//  - Dua tim: T dan CT. Ketuk blok toko di base untuk memilih tim DAN membuka toko (T = blok tembaga, CT = blok marmer).
//    Arena buatan mod ArenaCS (versi 1.1+) sudah menaruh kedua blok toko itu.
//  - Poin: tiap kill menambah skor tim. Skor target tercapai -> tim menang, skor dan uang direset.
//  - Uang: awal 800, +300 per kill atas lawan. Toko menjual senjata CS dan amunisi.
//  - Respawn: saat respawn pemain dipindah ke pad tim (berdasarkan posisi blok toko).
//  - Lawan: gelombang bot regu musuh (mod Tactical) muncul di base lawan setiap kali bot habis. Bot hanya menyerang di mode non-kreatif.
// Batasan: tim/uang hanya dilacak untuk pemain lokal (layar terbagi); bot hanya musuh (tidak ada bot sekutu); belum ada sinkron jaringan.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Engine;
using GameEntitySystem;

namespace Game
{
	public class TdmCsLoader : ModLoader
	{
		// ===== KONFIGURASI =====
		const int TargetScore = 30;
		const int StartMoney = 800;
		const int MaxMoney = 16000;
		const int KillReward = 300;
		const float WaveInterval = 12f;     // jeda antar gelombang bot setelah bot sebelumnya habis (detik)
		const int BotSquadSize = 3;         // 3 atau 5 (aturan mod Tactical)
		const int AmmoPackPrice = 150;
		const int AmmoPackCount = 3;
		const float ShopReach = 6f;

		static int TShopBlock() { return CopperBlock.Index; }
		static int CtShopBlock() { return MarbleBlock.Index; }

		enum Team { T, CT }

		sealed class PlayerState
		{
			public Team Team = Team.CT;
			public bool Joined;
			public int Money = StartMoney;
			public int Kills;
			public int Deaths;
		}

		sealed class Entry
		{
			public string Label;
			public int Price;
			public int Value;     // nilai item untuk inventori
			public int Count = 1;
		}

		static readonly ConditionalWeakTable<ComponentPlayer, PlayerState> s_states = new ConditionalWeakTable<ComponentPlayer, PlayerState>();

		static readonly Dictionary<string, int> s_prices = new Dictionary<string, int>
		{
			{ "glock18", 200 }, { "usp_silencer", 200 }, { "hkp2000", 200 }, { "p250", 300 }, { "elite", 400 },
			{ "fiveseven", 500 }, { "tec9", 500 }, { "cz75a", 500 }, { "revolver", 600 }, { "deagle", 700 },
			{ "mac10", 1050 }, { "nova", 1050 }, { "sawedoff", 1100 }, { "ump45", 1200 }, { "mp9", 1250 },
			{ "mag7", 1300 }, { "bizon", 1400 }, { "mp7", 1500 }, { "mp5sd", 1500 }, { "negev", 1700 },
			{ "ssg08", 1700 }, { "galilar", 1800 }, { "xm1014", 2000 }, { "famas", 2050 }, { "p90", 2350 },
			{ "ak47", 2700 }, { "m4a1s", 2900 }, { "sg556", 3000 }, { "m4a4", 3100 }, { "aug", 3300 },
			{ "awp", 4750 }, { "scar20", 5000 }, { "g3sg1", 5000 }, { "m249", 5200 }, { "taser", 200 }
		};
		const int DefaultGunPrice = 2500;

		static Project s_project;
		static bool s_matchActive;
		static int s_scoreT, s_scoreCT;
		static Point3? s_shopT, s_shopCT;       // posisi blok toko
		static Vector3? s_spawnT, s_spawnCT;    // titik respawn
		static Point3? s_groundT, s_groundCT;   // lantai pad (untuk spawn bot lawan)
		static float s_waveTimer;
		static bool s_warnedCreative;

		public override void __ModInitialize()
		{
			ModsManager.RegisterHook("OnProjectLoaded", this);
			ModsManager.RegisterHook("OnProjectDisposed", this);
			ModsManager.RegisterHook("OnPlayerInputInteract", this);
			ModsManager.RegisterHook("OnCreatureDied", this);
			ModsManager.RegisterHook("OnPlayerSpawned", this);
			ModsManager.RegisterHook("SubsystemUpdate", this);
		}

		public override void OnProjectLoaded(Project project)
		{
			s_project = project;
			s_matchActive = false;
			s_scoreT = s_scoreCT = 0;
			s_shopT = s_shopCT = null;
			s_spawnT = s_spawnCT = null;
			s_groundT = s_groundCT = null;
			s_waveTimer = 3f;
			s_warnedCreative = false;
		}

		public override void OnProjectDisposed()
		{
			s_project = null;
			s_matchActive = false;
		}

		static PlayerState Get(ComponentPlayer player)
		{
			return s_states.GetOrCreateValue(player);
		}

		static void Say(ComponentPlayer player, string text)
		{
			try
			{
				if (player != null && player.ComponentGui != null)
				{
					player.ComponentGui.DisplaySmallMessage(text, Color.White, false, false);
				}
			}
			catch (Exception)
			{
			}
		}

		static void SayAll(string text)
		{
			if (s_project == null)
			{
				return;
			}
			SubsystemPlayers players = s_project.FindSubsystem<SubsystemPlayers>(false);
			if (players == null)
			{
				return;
			}
			for (int i = 0; i < players.ComponentPlayers.Count; i++)
			{
				Say(players.ComponentPlayers[i], text);
			}
		}

		static string ScoreLine()
		{
			return "T " + s_scoreT + " - " + s_scoreCT + " CT  (target " + TargetScore + ")";
		}

		static Team? HostTeam()
		{
			if (s_project == null)
			{
				return null;
			}
			SubsystemPlayers players = s_project.FindSubsystem<SubsystemPlayers>(false);
			if (players == null || players.ComponentPlayers.Count == 0)
			{
				return null;
			}
			PlayerState s = Get(players.ComponentPlayers[0]);
			return s.Joined ? s.Team : (Team?)null;
		}

		// ===== toko =====
		public override void OnPlayerInputInteract(ComponentPlayer player, ref bool playerOperated, ref double timeIntervalLastActionTime, ref int priorityUse, ref int priorityInteract, ref int priorityPlace)
		{
			if (player == null || playerOperated || player.ComponentMiner == null || player.GameWidget == null || player.GameWidget.ActiveCamera == null)
			{
				return;
			}
			Camera camera = player.GameWidget.ActiveCamera;
			TerrainRaycastResult? hit = player.ComponentMiner.Raycast<TerrainRaycastResult>(new Ray3(camera.ViewPosition, camera.ViewDirection), RaycastMode.Interaction, true, false, false, ShopReach);
			if (!hit.HasValue)
			{
				return;
			}
			int contents = Terrain.ExtractContents(hit.Value.Value);
			Team team;
			if (contents == TShopBlock())
			{
				team = Team.T;
			}
			else if (contents == CtShopBlock())
			{
				team = Team.CT;
			}
			else
			{
				return;
			}
			playerOperated = true;
			priorityUse = 0;
			priorityInteract = 0;
			priorityPlace = 0;
			JoinAndOpenShop(player, team, new Point3(hit.Value.CellFace.X, hit.Value.CellFace.Y, hit.Value.CellFace.Z));
		}

		static void ScanShops(Point3 around)
		{
			SubsystemTerrain subsystemTerrain = s_project.FindSubsystem<SubsystemTerrain>(false);
			if (subsystemTerrain == null)
			{
				return;
			}
			Terrain terrain = subsystemTerrain.Terrain;
			int tBlock = TShopBlock();
			int ctBlock = CtShopBlock();
			Point3? foundT = null;
			Point3? foundCt = null;
			for (int x = around.X - 64; x <= around.X + 64 && !(foundT.HasValue && foundCt.HasValue); x++)
			{
				for (int z = around.Z - 64; z <= around.Z + 64; z++)
				{
					for (int y = Math.Max(around.Y - 3, 1); y <= Math.Min(around.Y + 3, 254); y++)
					{
						int c = terrain.GetCellContents(x, y, z);
						if (c == tBlock && !foundT.HasValue)
						{
							foundT = new Point3(x, y, z);
						}
						else if (c == ctBlock && !foundCt.HasValue)
						{
							foundCt = new Point3(x, y, z);
						}
					}
				}
			}
			if (foundT.HasValue) { s_shopT = foundT; }
			if (foundCt.HasValue) { s_shopCT = foundCt; }
			if (s_shopT.HasValue && s_shopCT.HasValue)
			{
				int dir = Math.Sign(s_shopCT.Value.Z - s_shopT.Value.Z);
				if (dir == 0) { dir = 1; }
				Point3 a = s_shopT.Value;
				Point3 b = s_shopCT.Value;
				s_spawnT = new Vector3(a.X + 0.5f, a.Y + 0.05f, a.Z + 0.5f + dir * 6f);
				s_spawnCT = new Vector3(b.X + 0.5f, b.Y + 0.05f, b.Z + 0.5f - dir * 6f);
				s_groundT = new Point3(a.X, a.Y - 1, a.Z + dir * 6);
				s_groundCT = new Point3(b.X, b.Y - 1, b.Z - dir * 6);
			}
		}

		static void JoinAndOpenShop(ComponentPlayer player, Team team, Point3 shopCell)
		{
			PlayerState state = Get(player);
			state.Team = team;
			if (!state.Joined)
			{
				state.Joined = true;
			}
			if (!(s_shopT.HasValue && s_shopCT.HasValue))
			{
				ScanShops(shopCell);
			}
			if (!s_matchActive)
			{
				s_matchActive = true;
				s_scoreT = s_scoreCT = 0;
				s_waveTimer = 3f;
				SayAll("Pertandingan TDM dimulai! " + ScoreLine());
			}
			if (!(s_spawnT.HasValue && s_spawnCT.HasValue))
			{
				Say(player, "Blok toko lawan tidak ditemukan; respawn dan bot musuh tidak aktif. Pakai arena ArenaCS.");
			}
			OpenShop(player, state);
		}

		static List<Entry> BuildEntries()
		{
			List<Entry> list = new List<Entry>();
			int gunBlock = BlocksManager.GetBlockIndex<ScGunBlock>(true, false);
			for (int v = 0; v < GunSpec.All.Length; v++)
			{
				GunSpec spec = GunSpec.All[v];
				int price;
				if (!s_prices.TryGetValue(spec.Name, out price))
				{
					price = DefaultGunPrice;
				}
				list.Add(new Entry
				{
					Label = spec.Name.ToUpperInvariant(),
					Price = price,
					Value = Terrain.MakeBlockValue(gunBlock, 0, GunSpec.MakeData(v, spec.Magazine)),
					Count = 1
				});
			}
			list.Sort((a, b) => a.Price.CompareTo(b.Price));
			list.Add(new Entry { Label = "Magazen x" + AmmoPackCount, Price = AmmoPackPrice, Value = ScAmmoBlock.Value(ScAmmoBlock.Magazine), Count = AmmoPackCount });
			list.Add(new Entry { Label = "Peluru shotgun x" + AmmoPackCount, Price = AmmoPackPrice, Value = ScAmmoBlock.Value(ScAmmoBlock.Shell), Count = AmmoPackCount });
			return list;
		}

		static void OpenShop(ComponentPlayer player, PlayerState state)
		{
			List<Entry> entries = BuildEntries();
			string title = "Toko " + state.Team + "  |  Uang $" + state.Money + "  |  " + ScoreLine();
			ListSelectionDialog dialog = new ListSelectionDialog(
				title,
				entries,
				56f,
				(Func<object, string>)(o =>
				{
					Entry e = (Entry)o;
					return e.Label + "  -  $" + e.Price;
				}),
				(Action<object>)(o => Buy(player, (Entry)o)));
			DialogsManager.ShowDialog(player.GuiWidget, dialog);
		}

		static void Buy(ComponentPlayer player, Entry entry)
		{
			if (entry == null || player == null || player.ComponentMiner == null)
			{
				return;
			}
			PlayerState state = Get(player);
			if (state.Money < entry.Price)
			{
				Say(player, "Uang kurang: butuh $" + entry.Price + ", punya $" + state.Money);
				return;
			}
			IInventory inventory = player.ComponentMiner.Inventory;
			int notAdded = ComponentInventoryBase.AcquireItems(inventory, entry.Value, entry.Count);
			if (notAdded >= entry.Count)
			{
				Say(player, "Inventori penuh.");
				return;
			}
			if (notAdded > 0)
			{
				Say(player, "Inventori hampir penuh, sebagian item tidak masuk.");
			}
			state.Money -= entry.Price;
			Say(player, "Dibeli: " + entry.Label + ". Sisa uang $" + state.Money);
		}

		// ===== skor =====
		static Team Opposite(Team t)
		{
			return t == Team.T ? Team.CT : Team.T;
		}

		static void AddScore(Team team)
		{
			if (team == Team.T) { s_scoreT++; } else { s_scoreCT++; }
		}

		static void AnnounceAndCheckWin(string prefix)
		{
			SayAll(prefix + ScoreLine());
			Team? winner = null;
			if (s_scoreT >= TargetScore) { winner = Team.T; }
			else if (s_scoreCT >= TargetScore) { winner = Team.CT; }
			if (!winner.HasValue)
			{
				return;
			}
			SayAll("Tim " + winner.Value + " menang! " + ScoreLine() + " - ronde baru dimulai.");
			s_scoreT = s_scoreCT = 0;
			SubsystemPlayers players = s_project.FindSubsystem<SubsystemPlayers>(false);
			if (players != null)
			{
				for (int i = 0; i < players.ComponentPlayers.Count; i++)
				{
					PlayerState s = Get(players.ComponentPlayers[i]);
					s.Money = StartMoney;
					s.Kills = 0;
					s.Deaths = 0;
				}
			}
		}

		public override void OnCreatureDied(ComponentHealth componentHealth, Injury injury, ref int experienceOrbDrop, ref bool calculateInKill)
		{
			if (!s_matchActive || s_project == null || componentHealth == null)
			{
				return;
			}
			Entity victim = componentHealth.Entity;
			Entity attacker = null;
			if (injury != null)
			{
				if (injury.Attackment != null && injury.Attackment.Attacker != null)
				{
					attacker = injury.Attackment.Attacker;
				}
				else if (injury.Attacker != null)
				{
					attacker = injury.Attacker.Entity;
				}
			}
			ComponentPlayer victimPlayer = victim.FindComponent<ComponentPlayer>();
			ComponentPlayer attackerPlayer = attacker != null ? attacker.FindComponent<ComponentPlayer>() : null;
			Team? host = HostTeam();

			if (victimPlayer != null)
			{
				PlayerState vs = Get(victimPlayer);
				if (!vs.Joined || attacker == victim)
				{
					return;   // bunuh diri / belum bergabung: tidak dihitung
				}
				vs.Deaths++;
				AddScore(Opposite(vs.Team));
				if (attackerPlayer != null)
				{
					PlayerState ass = Get(attackerPlayer);
					if (ass.Joined && ass.Team != vs.Team)
					{
						Reward(attackerPlayer, ass);
					}
				}
				AnnounceAndCheckWin("Tewas. ");
				return;
			}

			bool victimIsEnemyBot = victim.FindComponent<ComponentTacticalEnemy>() != null;
			bool victimIsAllyBot = victim.FindComponent<ComponentTacticalCompanion>() != null;
			if (!host.HasValue || !(victimIsEnemyBot || victimIsAllyBot))
			{
				return;
			}
			if (victimIsEnemyBot)
			{
				AddScore(host.Value);
				if (attackerPlayer != null)
				{
					PlayerState ass = Get(attackerPlayer);
					if (ass.Joined)
					{
						Reward(attackerPlayer, ass);
					}
				}
				AnnounceAndCheckWin("Bot musuh jatuh. ");
			}
			else
			{
				AddScore(Opposite(host.Value));
				AnnounceAndCheckWin("Sekutu jatuh. ");
			}
		}

		static void Reward(ComponentPlayer player, PlayerState state)
		{
			state.Kills++;
			state.Money = Math.Min(state.Money + KillReward, MaxMoney);
			Say(player, "+$" + KillReward + "  Uang $" + state.Money + "  Kill " + state.Kills);
		}

		// ===== respawn =====
		public override bool OnPlayerSpawned(PlayerData.SpawnMode spawnMode, ComponentPlayer componentPlayer, Vector3 position)
		{
			if (s_matchActive && componentPlayer != null)
			{
				PlayerState s = Get(componentPlayer);
				Vector3? spawn = s.Team == Team.T ? s_spawnT : s_spawnCT;
				if (s.Joined && spawn.HasValue && componentPlayer.ComponentBody != null)
				{
					componentPlayer.ComponentBody.Position = spawn.Value;
					componentPlayer.ComponentBody.Velocity = Vector3.Zero;
					Say(componentPlayer, "Respawn di base " + s.Team + ". Uang $" + s.Money + "  " + ScoreLine());
				}
			}
			return false;
		}

		// ===== gelombang bot =====
		public override void SubsystemUpdate(float dt)
		{
			if (!s_matchActive || s_project == null)
			{
				return;
			}
			s_waveTimer -= dt;
			if (s_waveTimer > 0f)
			{
				return;
			}
			s_waveTimer = WaveInterval;
			try
			{
				Team? host = HostTeam();
				SubsystemTacticalEnemies enemies = s_project.FindSubsystem<SubsystemTacticalEnemies>(false);
				if (!host.HasValue || enemies == null || enemies.Enemies.Count > 0)
				{
					return;
				}
				SubsystemGameInfo info = s_project.FindSubsystem<SubsystemGameInfo>(false);
				if (info != null && info.WorldSettings.GameMode == GameMode.Creative && !s_warnedCreative)
				{
					s_warnedCreative = true;
					SayAll("Mode kreatif: bot musuh tidak menyerang. Pakai mode Challenging/Harsh.");
				}
				Point3? ground = host.Value == Team.T ? s_groundCT : s_groundT;   // base lawan
				if (ground.HasValue)
				{
					enemies.SpawnManual(ground.Value, BotSquadSize);
				}
			}
			catch (Exception e)
			{
				Log.Error("TdmCS wave: " + e);
			}
		}
	}
}

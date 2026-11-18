// ArenaCS mod: membangun arena bergaya Counter-Strike (64x64) di sekitar pemain.
// Cara pakai: Pengaturan -> tombol "Bangun Arena CS" = Ya, lalu masuk ke dunia (sebaiknya dunia datar, mode kreatif).
// Setelah dunia dimuat dan chunk sekitar siap, arena dibangun bertahap, pemain dipindah ke pad spawn T, lalu tombol kembali Tidak.
// Versi 1.1: menambah blok toko T (tembaga) dan CT (marmer) di tiap base untuk mod TdmCS.
// Layout (lx = 0..63 ke arah X, lz = 0..63 ke arah Z): base T di lz kecil, base CT di lz besar,
// bomb site A/B (lantai besi) di sisi CT, tembok luar, tembok tengah, dan kotak penutup.
// Blok dan ukuran gampang diubah di bagian KONFIGURASI.
using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Engine;
using GameEntitySystem;

namespace Game
{
	public class ArenaCsLoader : ModLoader
	{
		public const string ConfigKey = "com.mrzgaming.arenacs.BuildNext";

		// ===== KONFIGURASI =====
		const int Size = 64;            // harus 64 (4x4 chunk, sejajar grid chunk)
		const int ClearHeight = 12;     // tinggi ruang yang dikosongkan di atas lantai
		const int OuterWallHeight = 8;
		const int ColumnsPerTick = 16;  // kolom diproses per update (kecil = lebih halus, lebih lama)
		const float WaitChunksSeconds = 40f;

		static int FloorBlock() { return Terrain.MakeBlockValue(SandstoneBlock.Index); }
		static int TFloorBlock() { return Terrain.MakeBlockValue(PlanksBlock.Index); }
		static int CtFloorBlock() { return Terrain.MakeBlockValue(GraniteBlock.Index); }
		static int SiteFloorBlock() { return Terrain.MakeBlockValue(IronBlock.Index); }
		static int OuterWallTBlock() { return Terrain.MakeBlockValue(BricksBlock.Index); }
		static int OuterWallCtBlock() { return Terrain.MakeBlockValue(CobblestoneBlock.Index); }
		static int WallBlock() { return Terrain.MakeBlockValue(CobblestoneBlock.Index); }
		static int CrateBlock() { return Terrain.MakeBlockValue(PlanksBlock.Index); }
		static int FillBlock() { return Terrain.MakeBlockValue(DirtBlock.Index); }

		struct Box
		{
			public int X1, X2, Z1, Z2, H;
			public bool Crate;
			public Box(int x1, int x2, int z1, int z2, int h, bool crate)
			{
				X1 = x1; X2 = x2; Z1 = z1; Z2 = z2; H = h; Crate = crate;
			}
		}

		// Dicerminkan ke sisi CT (lz -> 63 - lz).
		static readonly Box[] SymmetricBoxes = new Box[]
		{
			new Box(10, 13, 16, 19, 2, true),
			new Box(50, 53, 16, 19, 2, true),
			new Box(22, 29, 30, 33, 4, false),
			new Box(34, 41, 30, 33, 4, false),
			new Box(14, 15, 24, 29, 4, false),
			new Box(14, 15, 34, 39, 4, false),
			new Box(48, 49, 24, 29, 4, false),
			new Box(48, 49, 34, 39, 4, false),
			new Box(20, 21, 8, 12, 3, false),
			new Box(42, 43, 8, 12, 3, false),
			new Box(30, 33, 20, 21, 2, true),
		};

		// Hanya di sisi CT (sekitar bomb site).
		static readonly Box[] CtOnlyBoxes = new Box[]
		{
			new Box(10, 11, 48, 49, 2, true),
			new Box(14, 15, 50, 51, 2, true),
			new Box(52, 53, 48, 49, 2, true),
			new Box(48, 49, 50, 51, 2, true),
			new Box(6, 6, 42, 55, 3, false),
			new Box(57, 57, 42, 55, 3, false),
		};

		// Nilai blok untuk sel (lx, ly, lz). ly = 0 lantai, ly >= 1 di atas lantai. 0 = udara.
		static int Spec(int lx, int ly, int lz)
		{
			if (ly == 0)
			{
				if (lx >= 24 && lx <= 39 && lz >= 3 && lz <= 12) return TFloorBlock();
				if (lx >= 24 && lx <= 39 && lz >= 51 && lz <= 60) return CtFloorBlock();
				if (lz >= 44 && lz <= 53 && ((lx >= 8 && lx <= 17) || (lx >= 46 && lx <= 55))) return SiteFloorBlock();
				return FloorBlock();
			}
			// blok toko untuk mod TdmCS: tembaga = toko T, marmer = toko CT (ketuk untuk membuka)
			if (ly == 1 && (lx == 31 || lx == 32))
			{
				if (lz == 2) return Terrain.MakeBlockValue(CopperBlock.Index);
				if (lz == Size - 3) return Terrain.MakeBlockValue(MarbleBlock.Index);
			}
			if (ly <= OuterWallHeight && (lx == 0 || lx == Size - 1 || lz == 0 || lz == Size - 1))
			{
				return lz < Size / 2 ? OuterWallTBlock() : OuterWallCtBlock();
			}
			for (int i = 0; i < SymmetricBoxes.Length; i++)
			{
				Box b = SymmetricBoxes[i];
				if (ly <= b.H && lx >= b.X1 && lx <= b.X2)
				{
					if (lz >= b.Z1 && lz <= b.Z2) return b.Crate ? CrateBlock() : WallBlock();
					if (lz >= Size - 1 - b.Z2 && lz <= Size - 1 - b.Z1) return b.Crate ? CrateBlock() : WallBlock();
				}
			}
			for (int i = 0; i < CtOnlyBoxes.Length; i++)
			{
				Box b = CtOnlyBoxes[i];
				if (ly <= b.H && lx >= b.X1 && lx <= b.X2 && lz >= b.Z1 && lz <= b.Z2) return b.Crate ? CrateBlock() : WallBlock();
			}
			return 0;
		}

		// ===== status =====
		enum Phase { Idle, Waiting, Building }
		static Phase s_phase;
		static Project s_project;
		static SubsystemTerrain s_terrain;
		static ComponentPlayer s_player;
		static float s_waited;
		static int s_originX, s_originZ, s_floorY, s_nextColumn;
		static bool s_flag;
		static bool s_flagLoaded;

		static bool BuildNext
		{
			get
			{
				if (!s_flagLoaded)
				{
					string text;
					s_flag = ModsManager.Configs.TryGetValue(ConfigKey, out text) && text == "1";
					s_flagLoaded = true;
				}
				return s_flag;
			}
			set
			{
				s_flag = value;
				s_flagLoaded = true;
				ModsManager.SetConfig(ConfigKey, value ? "1" : "0");
				SettingsManager.SaveSettings();
			}
		}

		static string ButtonText()
		{
			return "Bangun Arena CS: " + (BuildNext ? "Ya (saat dunia dimuat)" : "Tidak");
		}

		public override void __ModInitialize()
		{
			ModsManager.RegisterHook("OnSettingsScreenCreated", this);
			ModsManager.RegisterHook("OnProjectLoaded", this);
			ModsManager.RegisterHook("OnProjectDisposed", this);
			ModsManager.RegisterHook("SubsystemUpdate", this);
		}

		public override void OnSettingsScreenCreated(SettingsScreen settingsScreen, out Dictionary<ButtonWidget, Action> buttonsToAdd)
		{
			ButtonWidget button = new BevelledButtonWidget
			{
				Name = "ArenaCsButton",
				Text = ButtonText(),
				Style = ContentManager.Get<XElement>("Styles/ButtonStyle_310x60"),
				HorizontalAlignment = WidgetAlignment.Center,
				VerticalAlignment = WidgetAlignment.Center,
				Margin = new Vector2(0f, 5f)
			};
			buttonsToAdd = new Dictionary<ButtonWidget, Action>();
			buttonsToAdd.Add(button, delegate
			{
				BuildNext = !BuildNext;
				button.Text = ButtonText();
			});
		}

		public override void OnProjectLoaded(Project project)
		{
			s_project = project;
			s_terrain = project.FindSubsystem<SubsystemTerrain>(false);
			s_player = null;
			s_waited = 0f;
			s_phase = (BuildNext && s_terrain != null) ? Phase.Waiting : Phase.Idle;
		}

		public override void OnProjectDisposed()
		{
			s_phase = Phase.Idle;
			s_project = null;
			s_terrain = null;
			s_player = null;
		}

		static void Say(string text)
		{
			try
			{
				if (s_player != null && s_player.ComponentGui != null)
				{
					s_player.ComponentGui.DisplaySmallMessage(text, Color.White, true, false);
				}
			}
			catch (Exception)
			{
			}
		}

		static bool ChunksReady()
		{
			Terrain terrain = s_terrain.Terrain;
			for (int cx = 0; cx < 4; cx++)
			{
				for (int cz = 0; cz < 4; cz++)
				{
					TerrainChunk chunk = terrain.GetChunkAtCell(s_originX + 16 * cx, s_originZ + 16 * cz);
					if (chunk == null || chunk.State < TerrainChunkState.InvalidLight)
					{
						return false;
					}
				}
			}
			return true;
		}

		public override void SubsystemUpdate(float dt)
		{
			if (s_phase == Phase.Idle || s_terrain == null || s_project == null)
			{
				return;
			}
			try
			{
				if (s_phase == Phase.Waiting)
				{
					UpdateWaiting(dt);
				}
				else
				{
					UpdateBuilding();
				}
			}
			catch (Exception e)
			{
				Log.Error("ArenaCS: " + e);
				Say("ArenaCS gagal: " + e.Message);
				s_phase = Phase.Idle;
				BuildNext = false;
			}
		}

		static void UpdateWaiting(float dt)
		{
			SubsystemPlayers players = s_project.FindSubsystem<SubsystemPlayers>(false);
			if (players == null || players.ComponentPlayers.Count == 0)
			{
				return;
			}
			if (s_player == null)
			{
				s_player = players.ComponentPlayers[0];
				Vector3 p = s_player.ComponentBody.Position;
				s_originX = (int)Math.Floor(p.X / 16f) * 16 - Size / 2;
				s_originZ = (int)Math.Floor(p.Z / 16f) * 16 - Size / 2;
				s_floorY = (int)Math.Floor(p.Y) - 1;   // blok tempat pemain berdiri
				s_floorY = Math.Clamp(s_floorY, 2, 200);
			}
			s_waited += dt;
			if (ChunksReady())
			{
				s_nextColumn = 0;
				s_phase = Phase.Building;
				Say("Membangun arena CS...");
			}
			else if (s_waited > WaitChunksSeconds)
			{
				Say("Arena batal: chunk sekitar belum termuat. Coba lagi atau naikkan jarak pandang.");
				s_phase = Phase.Idle;
				BuildNext = false;
			}
		}

		static void UpdateBuilding()
		{
			Terrain terrain = s_terrain.Terrain;
			int total = Size * Size;
			int end = Math.Min(s_nextColumn + ColumnsPerTick, total);
			for (; s_nextColumn < end; s_nextColumn++)
			{
				int lx = s_nextColumn % Size;
				int lz = s_nextColumn / Size;
				BuildColumn(terrain, lx, lz);
			}
			if (s_nextColumn >= total)
			{
				s_phase = Phase.Idle;
				BuildNext = false;
				// pindahkan pemain ke pad spawn T
				s_player.ComponentBody.Position = new Vector3(s_originX + 32.5f, s_floorY + 1.05f, s_originZ + 8.5f);
				s_player.ComponentBody.Velocity = Vector3.Zero;
				Say("Arena CS selesai. Anda di base T; base CT di seberang, bomb site di sisi CT.");
			}
		}

		static void BuildColumn(Terrain terrain, int lx, int lz)
		{
			int wx = s_originX + lx;
			int wz = s_originZ + lz;
			int top = terrain.CalculateTopmostCellHeight(wx, wz);
			int clearTop = Math.Min(Math.Max(top, s_floorY + ClearHeight), 254);
			// isi lubang di bawah lantai
			for (int y = Math.Max(top, 0); y < s_floorY; y++)
			{
				s_terrain.ChangeCell(wx, y, wz, FillBlock());
			}
			// lantai
			s_terrain.ChangeCell(wx, s_floorY, wz, Spec(lx, 0, lz));
			// ruang di atas lantai (udara + dinding/kotak)
			for (int y = s_floorY + 1; y <= clearTop; y++)
			{
				int ly = y - s_floorY;
				s_terrain.ChangeCell(wx, y, wz, ly <= ClearHeight ? Spec(lx, ly, lz) : 0);
			}
		}

	}
}

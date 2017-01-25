#region Copyright & License Information
/*
 * Copyright 2007-2016 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using OpenRA.Graphics;
using System.Drawing;
using OpenRA.Traits;

namespace OpenRA.Mods.yupgi_alert.Traits
{
	[Desc("Attach this to the world actor. Radioactivity layer, as in RA2 desolator radioactivity.", "Order of the layers defines the Z sorting.")]
	// You can attach this layer by editing rules/world.yaml
	// I (boolbada) made this layer by cloning resources layer, as resource amount is quite similar to
	// radio activity. I looked at SmudgeLayer too.

	public class RadioactivityLayerInfo : ITraitInfo
	{
		[Desc("Color of radio activity")]
		public readonly Color Color = Color.FromArgb(128, 255, 128); // tint factor sucks modify tint here statically.

		[Desc("Maximum radiation allowable in a cell.The cell can actually have more radiation but it will only damage as if it had the maximum level.")]
		public readonly int LevelMax = 500;

		//[Desc("Delay in ticks between radiation level decrements. The level updates this often, but the rate is still as specified in Halflife.")]
		public readonly int UpdateDelay = 15;

		[Desc("Scales the factor brightness plays in the radiation display.")]
		public readonly float LightFactor = 1.0f;

		[Desc("Scales the factor alpha plays in the radiation display.")]
		public readonly float AlphaFactor = 0.25f;

		[Desc("Delay of half life, in ticks")]
		public readonly int Halflife = 150; // in ticks.

		// Damage dealing is handled by "DamagedByRadioactivity" trait attached at each actor.
		public object Create(ActorInitializer init) { return new RadioactivityLayer(init.Self, this); }
	}

	class Radioactivity
	{
		public int ticks = 0;
		public int level = 0;

		public Radioactivity Clone()
		{
			Radioactivity result = new Radioactivity();
			result.ticks = ticks;
			result.level = level;
			return result;
		}
	}

	public class RadioactivityLayer : IRenderOverlay, IWorldLoaded, ITickRender, INotifyActorDisposing, ITick
	{
		readonly World world;
		readonly RadioactivityLayerInfo info;

		// In the following, I think dictionary is better than array, as radioactivity has similar affecting area as smudges.

		// true radioactivity values, without considering fog of war.
		readonly Dictionary<CPos, Radioactivity> tiles = new Dictionary<CPos, Radioactivity>();

		// what's visible to the player.
		readonly Dictionary<CPos, Radioactivity> rendered_tiles = new Dictionary<CPos, Radioactivity>();

		readonly HashSet<CPos> dirty = new HashSet<CPos>(); // dirty, as in cache dirty bits.

		readonly float k;

		public RadioactivityLayer(Actor self, RadioactivityLayerInfo info)
		{
			world = self.World;
			this.info = info;
			k = info.UpdateDelay * ((float) Math.Log(2)) / info.Halflife;
			//Debug.Assert(k > 0);
			// half life decay follows differential equation d/dt m(t) = -k m(t).
			// d/dt will be in ticks, ofcourse.
		}

		public void Render(WorldRenderer wr)
		{
			//foreach (var kv in spriteLayers.Values)
			//kv.Draw(wr.Viewport);
			foreach (var tile in rendered_tiles)
			{
				var ra = tile.Value;
				var center = wr.World.Map.CenterOfCell(tile.Key);
				var tl = wr.Screen3DPosition(center - new WVec(512, 512, 0)); // cos 512 is half a cell.
				var br = wr.Screen3DPosition(center + new WVec(512, 512, 0));

				int level = Math.Min(info.LevelMax, ra.level);

				//int r = (int)(info.Color.R * ra.level * info.LightFactor);
				//int g = (int)(info.Color.G * ra.level * info.LightFactor);
				//int b = (int)(info.Color.B * ra.level * info.LightFactor);
				int r = (int)(level * info.Color.R * info.LightFactor/info.LevelMax);
				int g = (int)(level * info.Color.G * info.LightFactor/info.LevelMax);
				int b = (int)(level * info.Color.B * info.LightFactor/info.LevelMax);
				int alpha = (int)(level * 255 * info.AlphaFactor/info.LevelMax);

				// make colors sane.
				//r = Math.Min(r, 255);
				//g = Math.Min(g, 255);
				//b = Math.Min(b, 255);
				//alpha = Math.Min(alpha, 255);

				Color color = Color.FromArgb(alpha, r, g, b);
				Game.Renderer.WorldRgbaColorRenderer.FillRect(tl, br, color);
			}
		}

		public void Tick(Actor self)
		{
			var remove = new List<CPos>();

			// Apply half life to each cell.
			foreach (var kv in tiles)
			{
				var ra = kv.Value;
				ra.ticks--; // count half-life.
				if (ra.ticks > 0)
					continue;

				// on each half life...
				//ra.ticks = info.Halflife; // reset ticks
				//ra.level /= 2; // simple is best haha...
				// Looks unnatural and induces "flickers"

				ra.ticks = info.UpdateDelay; // reset ticks
				int dlevel = (int)(k * ra.level);
				if (dlevel < 1)
					dlevel = 1; // must decrease by at least 1.
				ra.level -= dlevel;

				if (ra.level <= 0)
				{
					// Not radioactive anymore. Remove from this.tiles.
					remove.Add(kv.Key);
				}

				dirty.Add(kv.Key);
			}

			// Lets actually remove the entry.
			foreach (var r in remove)
				tiles.Remove(r);
		}

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			// hmm dunno what to do, at the moment.
		}

		// tick render, regardless of pause state.
		public void TickRender(WorldRenderer wr, Actor self)
		{
			var remove = new List<CPos>();
			foreach (var c in dirty)
			{
				if (!self.World.FogObscures(c))
				{
					// synchronize observations with true value.
					if (tiles.ContainsKey(c))
						rendered_tiles[c] = tiles[c].Clone();
					else
						rendered_tiles.Remove(c);
					remove.Add(c);
				}
			}

			foreach (var r in remove)
				dirty.Remove(r);
		}

		// Gets level, for damage calculation!!
		// That is, the level is constrained by LevelMax!!!!
		public int GetLevel(CPos cell)
		{
			if (!tiles.ContainsKey(cell))
				return 0;

			var level = tiles[cell].level;
			if (level > info.LevelMax)
				return info.LevelMax;
			else
				return level;
		}

		public void IncreaseLevel(CPos cell, int level, int max_level)
		{
			// Initialize, on fresh impact.
			if (!tiles.ContainsKey(cell))
				tiles[cell] = new Radioactivity();

			var ra = tiles[cell];
			var new_level = level + ra.level;

			if (new_level > max_level)
				new_level = max_level;

			if (ra.level > new_level)
				return; // the given weapon can't make the cell more radio active. (saturate)

			// apply new level.
			ra.level = new_level;

			ra.ticks = info.UpdateDelay;
			//ra.ticks = info.Halflife;

			dirty.Add(cell);
		}

		public void Destroy(CPos cell)
		{
			// Clear cell
			//Content[cell] = EmptyCell;
			//world.Map.CustomTerrain[cell] = byte.MaxValue;

			//dirty[cell] = 100;
		}
		
		bool disposed = false;
		public void Disposing(Actor self)
		{
			if (disposed)
				return;

			// dispose all stuff

			disposed = true;
		}
	}
}
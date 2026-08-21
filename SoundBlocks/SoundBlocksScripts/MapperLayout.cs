using System.Collections.Generic;
using BlockMapperInternal;
using UnityEngine;

namespace SoundBlocksMod
{
    /// <summary>
    /// Packs chosen mapper rows two to a row, by restacking the rows the mapper
    /// has already built. No reflection -- everything used here is public, which
    /// is what keeps the assembly loadable.
    ///
    /// The layout model:
    ///
    ///   * Rows stack contiguously by `Top`/`Bottom`, world-space edges: one
    ///     row's `Bottom` is the next one's `Top`, `Height` the difference.
    ///   * **Placement is Besiege's.** Assigning `Top` moves the container to
    ///     `value - TopOffset`, which is the game's own arithmetic; working the
    ///     position out here instead, through a hand-measured local-to-world
    ///     mapping, was only ever a way of getting the same answer less
    ///     reliably.
    ///   * `get_Top` reads `Background.position`, `set_Top` writes
    ///     `transform.position`. Those agree on every row the mapper builds
    ///     normally -- measured, they do -- so reading and writing `Top` is
    ///     safe on them, and only on them. See `Apply`.
    ///   * Only the two-column widths and offsets are done here, and those are
    ///     local, because `Background.localScale` is.
    ///   * A hidden widget gets no container at all.
    ///
    /// One mapper serves every block and pools its rows, so `Restore` hands back
    /// the row widths, the panel art and the title-bar buttons -- each only where
    /// it still carries the value this wrote. Row *positions* are the exception:
    /// the mapper lays those out itself, and writing remembered ones back is what
    /// strewed the Cannon's rows about.
    /// </summary>
    public static class MapperLayout
    {
        private static SoundBlocksBehaviour current;

        /// <summary>The panel art, with the size the game last gave it.</summary>
        private class Piece
        {
            public Transform T;
            public float FullLocalY;
            public float FullPosY;
            public float AppliedLocalY;
            public float AppliedPosY;
        }

        private static Piece background;
        private static BlockMapper panelOwner;
        private static UIScrollbar scrollbar;
        private static Transform closeButton;
        private static Transform resetButton;
        private static Vector3 closeStock;
        private static Vector3 resetStock;
        private static float buttonShift;
        private static bool buttonsKnown;
        private static Transform contentMask;

        /// <summary>
        /// Driven once a frame by MapperLayoutHost. Every frame, not once per
        /// open: any rebuild puts the rows back at their stock positions, and
        /// Apply is idempotent.
        /// </summary>
        public static void Tick()
        {
            BlockMapper mapper = BlockMapper.CurrentInstance;
            SoundBlocksBehaviour block = null;
            if (mapper != null && BlockMapper.IsOpen && mapper.IsBlock && mapper.Block != null)
            {
                block = mapper.Block.GetComponent<SoundBlocksBehaviour>();
            }

            if (block == null)
            {
                if (current != null)
                {
                    Restore();
                    current = null;
                }
                return;
            }

            current = block;
            block.EnsureVelocityMigrated();
            Attach(mapper);

            // Rebuild here rather than letting LateUpdate do it: showing or hiding
            // a slider marks the mapper dirty, and a rebuild after this has run
            // would draw the stock one-column layout for a frame. Rebuild() clears
            // IsDirty, so LateUpdate then finds nothing to do.
            if (mapper.IsDirty)
            {
                mapper.Rebuild();
            }

            Apply(mapper, block.LayoutRows());
        }

        /// <summary>
        /// Takes hold of the one shared mapper: finds the pieces this moves, and
        /// stops it scrolling.
        ///
        /// The pane always has room for the whole block once the rows are paired
        /// up, so scrolling has nothing left to do -- but Besiege decides that for
        /// itself, from the stock layout, before any of this has run. So the
        /// component is switched off outright, which stops the wheel and the drag
        /// as well as the bar, and the content is put back to its origin: anything
        /// already scrolled off the top would otherwise stay off it, since the
        /// restack takes its start from wherever the first row currently sits.
        /// </summary>
        private static void Attach(BlockMapper mapper)
        {
            if (panelOwner == mapper)
            {
                return;
            }
            panelOwner = mapper;

            // The panel art, and nothing else. `Container/Mask` is the scrollbar's
            // contentMask -- a clipping region the game sizes to roughly a
            // screenful. Shrinking that to the content clipped away every *other*
            // block's rows, since one mapper serves them all.
            background = NewPiece(mapper.transform.Find("Background"));
            closeButton = ButtonTransform(mapper.CloseButton);
            resetButton = ButtonTransform(mapper.ResetButton);
            buttonsKnown = false;

            scrollbar = mapper.GetComponentInChildren<UIScrollbar>(true);
            contentMask = null;
            if (scrollbar != null)
            {
                contentMask = scrollbar.contentMask;
                scrollbar.ResetContentPos();
                LearnButtons(mapper);
            }
        }

        private class Saved
        {
            public Transform Background;
            public float FullWidth;
            public float HalvedWidth;
        }

        private static readonly List<Saved> saved = new List<Saved>();

        /// <summary>
        /// Lays the given rows out, one entry per visual row: an array of one
        /// MapperType keeps a row to itself, an array of two shares one. Rows whose
        /// members are all hidden vanish, and the whole stack below closes up.
        /// </summary>
        private static void Apply(BlockMapper mapper, List<MapperType[]> rows)
        {
            if (mapper == null)
            {
                return;
            }

            ContainerDetails[] found = mapper.GetComponentsInChildren<ContainerDetails>(true);
            if (found == null || found.Length == 0)
            {
                return;
            }

            // Real rows only. The mapper keeps a zero-height anchor that is not one.
            List<ContainerDetails> stack = new List<ContainerDetails>();
            for (int i = 0; i < found.Length; i++)
            {
                ContainerDetails c = found[i];
                if (c != null && c.gameObject.activeInHierarchy && c.Height > 0f)
                {
                    stack.Add(c);
                }
            }
            if (stack.Count == 0)
            {
                return;
            }

            // Top of the panel first. Top is world-space and rows are contiguous,
            // so this is the order the mapper drew them in.
            SortByTopDescending(stack);

            // Group the stack into output rows.
            int firstOwned;
            List<ContainerDetails[]> output = BuildOutput(stack, rows, out firstOwned);
            if (output == null)
            {
                return;
            }

            // A row's full width, measured as the widest on screen: the unpaired
            // rows are always full width. Measured rather than taken from the row
            // about to be halved, which would halve again every frame.
            float fullWidth = 0f;
            float centreX = 0f;
            for (int i = 0; i < stack.Count; i++)
            {
                Transform bg = stack[i].Background;
                if (bg != null && bg.localScale.x > fullWidth)
                {
                    fullWidth = bg.localScale.x;
                    centreX = stack[i].transform.localPosition.x;
                }
            }

            // Everything above the first row of ours is Besiege's to place and is
            // left alone; the restack begins at the bottom of the last of them.
            //
            // Not merely tidiness. `get_Top` reads `Background.position` while
            // `set_Top` writes `transform.position`, so reading a row's Top and
            // writing it back is only a no-op while those two agree. The key row
            // is where that stops holding: its KeyContainer overrides GetHeight to
            // take the height from `VariableMapperHeight` once the key is in
            // variable mode, and `ExpandBackgroundToMapperHeight` rescales the
            // selector's own plate to match. Doing that round trip on it every
            // frame walked the row down and opened a gap above it.
            //
            // The cursor still follows those rows: one that grows moves its own
            // Bottom, so everything below shifts by exactly as much.
            float cursor = firstOwned > 0
                ? output[firstOwned - 1][0].Bottom
                : stack[0].Top;

            for (int i = firstOwned; i < output.Count; i++)
            {
                ContainerDetails[] row = output[i];

                float height = 0f;
                for (int j = 0; j < row.Length; j++)
                {
                    if (row[j].Height > height)
                    {
                        height = row[j].Height;
                    }
                }

                float top = cursor;

                for (int j = 0; j < row.Length; j++)
                {
                    // Besiege's own placement, offsets and all.
                    //
                    // `Bottom` is deliberately not written as well: it is a second
                    // call to set_position, derived from BottomOffset, and agrees
                    // with Top only while the row's height is the one used to
                    // place it -- not so for the shorter half of a mismatched pair.
                    row[j].Top = top;
                }

                if (row.Length == 2)
                {
                    SideBySide(row[0], row[1], fullWidth, centreX);
                }

                cursor -= height;
            }

            FitPanel(mapper, cursor);
            Remeasure();
            PlaceButtons();
        }

        /// <summary>
        /// Sits the panel's bottom edge on the bottom of the content, top edge
        /// left where it is.
        ///
        /// Needed because `UpdateBackground` sizes the panel from a
        /// `WidgetController.EndPosition` that always describes the *uncompacted*
        /// layout, and both the setter and the controller are private.
        ///
        /// The target is absolute -- the content's own bottom edge. Every
        /// delta-based attempt failed: shrinking by how far a pass moved things
        /// reads zero once the rows are compacted, and shrinking by the summed
        /// height difference is right only until it is applied twice.
        /// </summary>
        private static void FitPanel(BlockMapper mapper, float contentBottom)
        {
            // Never past the clip line. The panel art is a sibling of the mask,
            // not inside it, so it is not clipped: run it down to the content and
            // it carries on below the last row Besiege will actually draw, which
            // is where the band of empty background came from. Where the content
            // is longer than the mask the pane fills to the mask and the game's
            // own scrollbar reaches the rest.
            if (contentMask != null)
            {
                float maskBottom = contentMask.position.y - contentMask.lossyScale.y * 0.5f;
                if (maskBottom > contentBottom)
                {
                    contentBottom = maskBottom;
                }
            }

            Piece p = background;
            if (p == null || p.T == null)
            {
                return;
            }

            // Any value but our own last write means the game has resized it, and
            // that is the full size to measure from now.
            if (Mathf.Abs(p.T.localScale.y - p.AppliedLocalY) > 0.0001f ||
                Mathf.Abs(p.T.position.y - p.AppliedPosY) > 0.0001f)
            {
                p.FullLocalY = p.T.localScale.y;
                p.FullPosY = p.T.position.y;
            }

            float localY = p.T.localScale.y;
            if (localY < 0.0001f && localY > -0.0001f)
            {
                return;
            }
            // lossyScale carries the parent chain, so no factor is hardcoded.
            float chain = p.T.lossyScale.y / localY;
            float fullHeight = p.FullLocalY * chain;
            float topEdge = p.FullPosY + fullHeight * 0.5f;

            // Unclamped, so the window extends downwards when a slider appears
            // rather than scrolling. Clamping to the game's own height is what
            // left it untouched whenever new content ran past the bottom.
            float wanted = topEdge - contentBottom;
            if (wanted <= 0.0001f)
            {
                return;
            }

            p.AppliedLocalY = p.FullLocalY * wanted / fullHeight;
            p.AppliedPosY = topEdge - wanted * 0.5f;

            Vector3 ls = p.T.localScale;
            p.T.localScale = new Vector3(ls.x, p.AppliedLocalY, ls.z);
            Vector3 pos = p.T.position;
            p.T.position = new Vector3(pos.x, p.AppliedPosY, pos.z);
        }

        /// <summary>
        /// Re-measures the content now that it has been compacted, which is the
        /// whole of "no scrolling".
        ///
        /// `UIScrollbar.UpdateBounds` sets `contentSize` from the union of the
        /// rows' renderer bounds and calls `DisableScrollbar` itself when that
        /// comes out shorter than the mask. Its `Update` then early-returns on the
        /// same test, so the wheel does nothing -- exactly as on a block whose
        /// options fit. Besiege's own measurement is taken during a rebuild, from
        /// the stock one-column layout, so it never sees the compacted height.
        ///
        /// The component itself must stay enabled. `Update` is also what sets the
        /// static `stopCamZoom` while the cursor is over the pane; with it off,
        /// the wheel falls through to the camera and zooms the level.
        /// </summary>
        private static void Remeasure()
        {
            if (scrollbar != null && scrollbar.contentParent != null)
            {
                scrollbar.UpdateBounds();
            }
        }

        /// <summary>
        /// Works out where the two right-hand title-bar buttons sit with the
        /// scrollbar off, from wherever the game has them now.
        ///
        /// `UpdateBackground` places them at `stock + right * 0.2 * scale.x * 0.75`
        /// while the scrollbar is active, and writes them only on a *change* of
        /// `active`. Called from Attach, before any re-measure of ours, so the
        /// positions still match what the game last concluded.
        /// </summary>
        private static void LearnButtons(BlockMapper mapper)
        {
            buttonsKnown = true;
            buttonShift = 0.2f * mapper.transform.localScale.x * 0.75f;
            Vector3 off = scrollbar.active ? Vector3.right * buttonShift : Vector3.zero;
            if (closeButton != null)
            {
                closeStock = closeButton.localPosition - off;
            }
            if (resetButton != null)
            {
                resetStock = resetButton.localPosition - off;
            }
        }

        /// <summary>
        /// Puts the buttons where the current scrollbar state calls for.
        ///
        /// Run after the re-measure, because that is what settles whether this
        /// block scrolls, and Besiege will not move them for us: it re-reads
        /// `active` from the stock row layout on every rebuild, so it never sees
        /// the compacted pane's answer either way.
        /// </summary>
        private static void PlaceButtons()
        {
            if (!buttonsKnown || scrollbar == null)
            {
                return;
            }
            Vector3 off = scrollbar.active ? Vector3.right * buttonShift : Vector3.zero;
            if (closeButton != null)
            {
                closeButton.localPosition = closeStock + off;
            }
            if (resetButton != null)
            {
                resetButton.localPosition = resetStock + off;
            }
        }

        /// <summary>The transform of one of the mapper's own title-bar buttons.</summary>
        private static Transform ButtonTransform(Component button)
        {
            return button == null ? null : button.transform;
        }

        private static Piece NewPiece(Transform t)
        {
            if (t == null)
            {
                return null;
            }
            Piece p = new Piece();
            p.T = t;
            p.FullLocalY = t.localScale.y;
            p.FullPosY = t.position.y;
            p.AppliedLocalY = float.NaN;     // nothing applied yet
            p.AppliedPosY = float.NaN;
            return p;
        }

        /// <summary>
        /// Undoes everything that would otherwise outlive this block's mapper.
        ///
        /// Row *positions* are deliberately not restored: the mapper pools its
        /// rows, so the containers recorded here may already have been rebuilt
        /// into another block's mapper, and writing remembered Top/Bottom onto
        /// those is what left the Cannon's rows strewn down its panel. They need
        /// no undo anyway -- the next Rebuild lays them out itself.
        ///
        /// Widths, the panel art and the buttons do, because nothing else sets
        /// them back. Each is reverted only where it still carries our value.
        /// </summary>
        private static void Restore()
        {
            for (int i = 0; i < saved.Count; i++)
            {
                Saved s = saved[i];
                if (s.Background == null)
                {
                    continue;
                }
                Vector3 now = s.Background.localScale;
                if (Mathf.Abs(now.x - s.HalvedWidth) < 0.001f)
                {
                    s.Background.localScale = new Vector3(s.FullWidth, now.y, now.z);
                }
            }
            saved.Clear();

            // The panel art, the scrollbar and the title-bar buttons belong to the
            // one shared mapper, so unlike the pooled rows they are handed back --
            // each only where it still carries the value this wrote.
            Piece p = background;
            if (p != null && p.T != null && !float.IsNaN(p.AppliedLocalY))
            {
                if (Mathf.Abs(p.T.localScale.y - p.AppliedLocalY) < 0.0001f)
                {
                    Vector3 ls = p.T.localScale;
                    p.T.localScale = new Vector3(ls.x, p.FullLocalY, ls.z);
                }
                if (Mathf.Abs(p.T.position.y - p.AppliedPosY) < 0.0001f)
                {
                    Vector3 pos = p.T.position;
                    p.T.position = new Vector3(pos.x, p.FullPosY, pos.z);
                }
            }
            background = null;

            // Let the game work out for itself whether the next block scrolls,
            // then leave the buttons where that answer puts them.
            Remeasure();
            PlaceButtons();
            buttonsKnown = false;
            contentMask = null;
            scrollbar = null;
            closeButton = null;
            resetButton = null;
            panelOwner = null;
        }

        // ---- placement -------------------------------------------------------

        /// <summary>
        /// The output row order: rows this does not own keep their place and
        /// order; the owned ones go in as a block, where the first of them was.
        /// <paramref name="firstOwned"/> is where that block starts, which is also
        /// the first row the caller is allowed to move.
        /// </summary>
        private static List<ContainerDetails[]> BuildOutput(
            List<ContainerDetails> stack, List<MapperType[]> rows, out int firstOwned)
        {
            firstOwned = 0;
            // Resolve the wanted rows against what is actually on screen.
            List<ContainerDetails[]> wanted = new List<ContainerDetails[]>();
            List<ContainerDetails> mine = new List<ContainerDetails>();
            for (int i = 0; i < rows.Count; i++)
            {
                List<ContainerDetails> present = new List<ContainerDetails>();
                for (int j = 0; j < rows[i].Length; j++)
                {
                    ContainerDetails c = FindByType(stack, rows[i][j]);
                    if (c != null)
                    {
                        present.Add(c);
                        mine.Add(c);
                    }
                }
                if (present.Count > 0)
                {
                    wanted.Add(present.ToArray());
                }
            }
            if (mine.Count == 0)
            {
                return null;
            }

            List<ContainerDetails[]> output = new List<ContainerDetails[]>();
            bool placed = false;
            for (int i = 0; i < stack.Count; i++)
            {
                ContainerDetails c = stack[i];
                if (Contains(mine, c))
                {
                    if (!placed)
                    {
                        placed = true;
                        firstOwned = output.Count;
                        for (int j = 0; j < wanted.Count; j++)
                        {
                            output.Add(wanted[j]);
                        }
                    }
                    continue;       // every one of mine is already in wanted
                }
                output.Add(new ContainerDetails[] { c });
            }
            return output;
        }

        /// <summary>
        /// Halves two rows into two columns. Widths and offsets are the one thing
        /// done in local units, because Background.localScale is local.
        /// </summary>
        private static void SideBySide(ContainerDetails left, ContainerDetails right, float width, float centre)
        {
            Transform lbg = left.Background;
            Transform rbg = right.Background;
            if (lbg == null || rbg == null || width <= 0f)
            {
                return;                 // nothing measurable; leave them full width
            }

            // No gap: the halved thing is the row's backing plate, not the
            // button, so a gap shows as a lighter seam of bare panel. The buttons
            // carry their own inset and still read as two columns.
            float half = width * 0.5f;
            float shift = half * 0.5f;

            RecordHalved(lbg, width, half);
            RecordHalved(rbg, width, half);

            Vector3 ls = lbg.localScale;
            lbg.localScale = new Vector3(half, ls.y, ls.z);
            Vector3 rs = rbg.localScale;
            rbg.localScale = new Vector3(half, rs.y, rs.z);

            // Both columns go against `centre`, taken from a row this never moves.
            // Reading the moved row's own x shifts it again every pass, and Apply
            // runs every frame -- the columns march off the panel in a second.
            Vector3 lp = left.transform.localPosition;
            left.transform.localPosition = new Vector3(centre - shift, lp.y, lp.z);
            Vector3 rp = right.transform.localPosition;
            right.transform.localPosition = new Vector3(centre + shift, rp.y, rp.z);
        }

        private static void SortByTopDescending(List<ContainerDetails> list)
        {
            for (int i = 1; i < list.Count; i++)
            {
                ContainerDetails key = list[i];
                int j = i - 1;
                while (j >= 0 && list[j].Top < key.Top)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = key;
            }
        }

        private static ContainerDetails FindByType(List<ContainerDetails> stack, MapperType type)
        {
            if (type == null || !type.DisplayInMapper)
            {
                return null;
            }
            for (int i = 0; i < stack.Count; i++)
            {
                ContainerDetails c = stack[i];
                if (c.selector != null && c.selector.MapperType == type)
                {
                    return c;
                }
            }
            return null;
        }

        private static bool Contains(List<ContainerDetails> list, ContainerDetails c)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == c)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Notes a plate this halved, so Restore can put its width back.</summary>
        private static void RecordHalved(Transform background, float full, float halved)
        {
            for (int i = 0; i < saved.Count; i++)
            {
                if (saved[i].Background == background)
                {
                    return;
                }
            }
            Saved s = new Saved();
            s.Background = background;
            s.FullWidth = full;
            s.HalvedWidth = halved;
            saved.Add(s);
        }
    }
}

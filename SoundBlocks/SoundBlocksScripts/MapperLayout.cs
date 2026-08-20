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
    /// The layout model, measured from a running mapper rather than assumed:
    ///
    ///   * Rows stack contiguously by `Top`/`Bottom`, world-space edges: one
    ///     row's `Bottom` is the next one's `Top`, `position.y` their midpoint,
    ///     `Height` the difference.
    ///   * `localPosition` is in another scale (the rows' parent is scaled 0.9),
    ///     so vertical work is done in world units and converted back through a
    ///     measured mapping. Only widths are local.
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
        private static Vector3 closeApplied;
        private static Vector3 resetApplied;
        private static bool buttonsShifted;

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
            buttonsShifted = false;

            scrollbar = mapper.GetComponentInChildren<UIScrollbar>(true);
            if (scrollbar != null)
            {
                scrollbar.ResetContentPos();
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
            if (stack.Count < 2)
            {
                return;
            }

            // Top of the panel first. Top is world-space and rows are contiguous,
            // so this is the order the mapper drew them in.
            SortByTopDescending(stack);

            // The local <-> world mapping, measured rather than assumed:
            // worldY = scale * localY + offset.
            float scale, offset;
            if (!MeasureMapping(stack, out scale, out offset))
            {
                return;
            }

            // Group the stack into output rows.
            List<ContainerDetails[]> output = BuildOutput(stack, rows);
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

            // Restack contiguously from where the stack already began, so the
            // header and anything above the toggles land exactly where they were.
            float cursor = stack[0].Top;

            for (int i = 0; i < output.Count; i++)
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
                float bottom = cursor - height;
                float centre = (top + bottom) * 0.5f;

                for (int j = 0; j < row.Length; j++)
                {
                    row[j].Top = top;
                    row[j].Bottom = bottom;
                    SetWorldCentre(row[j], centre, scale, offset);
                }

                if (row.Length == 2)
                {
                    SideBySide(row[0], row[1], fullWidth, centreX);
                }

                cursor = bottom;
            }

            FitPanel(mapper, cursor);
            Remeasure();
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
            StockScrollbar(mapper);

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
        /// Puts the two right-hand title-bar buttons where a mapper without a
        /// scrollbar keeps them.
        ///
        /// Besiege moves them only from `UpdateBackground`, only on a *change* of
        /// `scrollbar.active`, and it re-measures `active` from the stock row
        /// layout on every rebuild -- before any of this has run. So it concludes
        /// the sound block scrolls, shifts the buttons along, and never sees the
        /// scrollbar go away again. The offset undone here is its own formula.
        /// </summary>
        private static void StockScrollbar(BlockMapper mapper)
        {
            if (scrollbar == null)
            {
                return;
            }

            if (scrollbar.active && !buttonsShifted)
            {
                // Read the shifted positions while they are still the game's own;
                // from here the targets are absolute.
                buttonsShifted = true;
                float shift = 0.2f * mapper.transform.localScale.x * 0.75f;
                if (closeButton != null)
                {
                    closeStock = closeButton.localPosition;
                    closeApplied = closeStock - Vector3.right * shift;
                }
                if (resetButton != null)
                {
                    resetStock = resetButton.localPosition;
                    resetApplied = resetStock - Vector3.right * shift;
                }
            }

            if (buttonsShifted)
            {
                if (closeButton != null)
                {
                    closeButton.localPosition = closeApplied;
                }
                if (resetButton != null)
                {
                    resetButton.localPosition = resetApplied;
                }
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

            if (buttonsShifted)
            {
                if (closeButton != null && closeButton.localPosition == closeApplied)
                {
                    closeButton.localPosition = closeStock;
                }
                if (resetButton != null && resetButton.localPosition == resetApplied)
                {
                    resetButton.localPosition = resetStock;
                }
                buttonsShifted = false;
            }

            // Let the game work out for itself whether the next block scrolls.
            Remeasure();
            scrollbar = null;
            closeButton = null;
            resetButton = null;
            panelOwner = null;
        }

        // ---- placement -------------------------------------------------------

        /// <summary>
        /// The output row order: rows this does not own keep their place and
        /// order; the owned ones go in as a block, where the first of them was.
        /// </summary>
        private static List<ContainerDetails[]> BuildOutput(
            List<ContainerDetails> stack, List<MapperType[]> rows)
        {
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

        /// <summary>Moves a row so its world-space midpoint lands on <paramref name="centre"/>.</summary>
        private static void SetWorldCentre(ContainerDetails c, float centre, float scale, float offset)
        {
            Vector3 p = c.transform.localPosition;
            c.transform.localPosition = new Vector3(p.x, (centre - offset) / scale, p.z);
        }

        // ---- measurement -----------------------------------------------------

        /// <summary>
        /// Solves worldY = scale * localY + offset from two rows at different
        /// heights. Measured, because the rows' parent is scaled by an amount
        /// that is not a mod's to assume.
        /// </summary>
        private static bool MeasureMapping(List<ContainerDetails> stack, out float scale, out float offset)
        {
            scale = 1f;
            offset = 0f;
            ContainerDetails a = stack[0];
            for (int i = 1; i < stack.Count; i++)
            {
                ContainerDetails b = stack[i];
                float dLocal = a.transform.localPosition.y - b.transform.localPosition.y;
                if (dLocal > 0.0001f || dLocal < -0.0001f)
                {
                    float aCentre = (a.Top + a.Bottom) * 0.5f;
                    float bCentre = (b.Top + b.Bottom) * 0.5f;
                    scale = (aCentre - bCentre) / dLocal;
                    if (scale > -0.0001f && scale < 0.0001f)
                    {
                        return false;
                    }
                    offset = aCentre - scale * a.transform.localPosition.y;
                    return true;
                }
            }
            return false;
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

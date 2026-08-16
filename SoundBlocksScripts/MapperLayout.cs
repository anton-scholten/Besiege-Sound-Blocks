using System.Collections.Generic;
using BlockMapperInternal;
using UnityEngine;

namespace SoundBlocksMod
{
    /// <summary>
    /// Packs chosen mapper rows two to a row. Besiege has no supported way to do
    /// this, so it restacks the rows the mapper has already built -- without
    /// reflection, which is what keeps it loadable: `BlockMapper.CurrentInstance`
    /// and `ContainerDetails`' `Top`/`Bottom` setters and `selector`/`Background`
    /// fields are all public.
    ///
    /// The layout model, measured from a running mapper rather than assumed:
    ///
    ///   * Rows stack *contiguously* by `Top`/`Bottom`, which are world-space
    ///     edges -- one row's `Bottom` is the next row's `Top`, `position.y` is
    ///     their midpoint, `Height` is the difference.
    ///   * `localPosition` is in a *different* scale: the rows' parent is scaled
    ///     0.9, so a local pitch of 0.6 is a world pitch of 0.54. Everything
    ///     vertical is therefore computed in world units and converted back
    ///     through the measured mapping. Only widths are local.
    ///   * A hidden widget gets no container at all.
    ///
    /// Everything touched is recorded and put back by `Restore`. That is not
    /// tidiness: the mapper pools its rows, so a row left half-width here comes
    /// back half-width in the next block's mapper.
    /// </summary>
    public static class MapperLayout
    {
        /// <summary>
        /// Gap between the two columns, as a fraction of the row width. Zero on
        /// purpose: the halved thing is the row's *backing plate*, not the button,
        /// so any gap here shows as a lighter vertical seam of bare panel running
        /// the height of the toggle block. The buttons carry their own inset, so
        /// butting the plates together still leaves them visibly apart.
        /// </summary>
        private const float ColumnGap = 0f;

        /// <summary>
        /// Set false to leave the mapper stock and log its geometry instead, which
        /// is how the placement was worked out. Reach for this before adjusting
        /// anything here by eye.
        /// </summary>
        public static bool Compact = true;

        private static SoundBlocksBehaviour current;
        private static bool dumped;

        private class Piece
        {
            public Transform T;
            public float FullLocalY;
            public float FullPosY;
            public float AppliedLocalY;
            public float AppliedPosY;
        }

        private static readonly List<Piece> panel = new List<Piece>();
        private static BlockMapper panelOwner;
        private static Transform scrollbar;

        /// <summary>
        /// Driven once a frame by MapperLayoutHost, for the whole session.
        ///
        /// Every frame rather than once per open, because BlockMapper.LateUpdate
        /// calls Rebuild() whenever the mapper is dirty -- which showing or hiding
        /// a slider makes it -- and a rebuild puts every row back at its stock
        /// position. Re-applying is safe because Apply is idempotent.
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
                    dumped = false;
                }
                return;
            }

            current = block;
            block.EnsureVelocityMigrated();

            // Rebuild the mapper here rather than letting its own LateUpdate do it.
            // Showing or hiding a slider marks it dirty; LateUpdate then rebuilds
            // every row back to stock *after* this has run, so the stock one-column
            // layout is what gets drawn for that frame. Rebuild() ends by clearing
            // IsDirty, so doing it first means LateUpdate finds nothing to do and
            // the re-layout below lands before anything is drawn.
            if (mapper.IsDirty)
            {
                mapper.Rebuild();
            }

            if (Compact)
            {
                Apply(mapper, block.LayoutRows());
            }
            else if (!dumped)
            {
                dumped = true;
                Dump(mapper);
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
        public static void Apply(BlockMapper mapper, List<MapperType[]> rows)
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

            // Group the stack into output rows: everything that is not one of mine
            // keeps its own row and its original order; my rows are placed as a
            // block, where the first of them used to start.
            List<ContainerDetails[]> output = BuildOutput(stack, rows);
            if (output == null)
            {
                return;
            }

            // The natural full width of a row, taken as the widest one on screen.
            // Rows this does not pair -- the key, the menus, the sliders -- are
            // always full width, so this is the panel's own row width.
            //
            // It must come from a measurement like this rather than from the row
            // about to be halved: Apply runs every frame, so halving whatever
            // width is already there would halve again on every frame until the
            // buttons vanished.
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
        }

        /// <summary>
        /// Sits the panel's bottom edge on the bottom of the content, with its top
        /// edge left where it is.
        ///
        /// The panel is sized by `UpdateBackground` from its `WidgetController`'s
        /// `EndPosition`, which always describes the *uncompacted* layout, so it
        /// runs past the last row once this has restacked. `set_EndPosition` is
        /// private and the controller is a private field, so the panel's own
        /// objects are resized instead, found by name from a dump of the hierarchy.
        ///
        /// The target is absolute -- the content's own bottom edge -- and that is
        /// the whole point. Every earlier attempt shrank by a *delta*, and each one
        /// failed differently: shrinking by how far a pass moved things reads zero
        /// once the rows are already compacted, even though the panel may have been
        /// re-expanded since (which is exactly what showing or hiding a slider
        /// does); and shrinking by the summed height difference is right only until
        /// it is applied twice. A target computed from where the content actually
        /// ends cannot go stale or compound, whatever order things happen in.
        ///
        /// Fitting the bottom to the content, rather than merely lifting it, is
        /// also what settles the scrollbar: Besiege's own layout leaves the content
        /// hanging slightly below the panel, so a panel that only moves up by the
        /// height saved still overflows and still shows room below.
        /// </summary>
        private static void FitPanel(BlockMapper mapper, float contentBottom)
        {
            if (panelOwner != mapper)
            {
                panelOwner = mapper;
                panel.Clear();
                Transform root = mapper.transform;
                AddPiece(root.Find("Background"));
                AddPiece(root.Find("Container/Mask"));
                scrollbar = root.Find("Container/Scrollbar");
            }

            // Nothing scrolls any more, so the bar is only clutter. It is the
            // mapper's own object, shared with every block, so Restore puts it back.
            if (scrollbar != null && scrollbar.gameObject.activeSelf)
            {
                scrollbar.gameObject.SetActive(false);
            }

            for (int i = 0; i < panel.Count; i++)
            {
                Piece p = panel[i];
                if (p.T == null)
                {
                    continue;
                }

                // Anything but our own last write means the game has resized it --
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
                    continue;
                }
                // The parent chain's scale, so no factor is assumed anywhere.
                float chain = p.T.lossyScale.y / localY;
                float fullHeight = p.FullLocalY * chain;
                float topEdge = p.FullPosY + fullHeight * 0.5f;

                float wanted = topEdge - contentBottom;
                if (wanted <= 0.0001f)
                {
                    continue;
                }
                // No clamp to the game's own height: the panel grows as well as
                // shrinks, so adding a slider extends the window downwards instead
                // of scrolling. Clamping here is what left the panel untouched
                // whenever a newly shown slider pushed the content past it.

                float newLocalY = p.FullLocalY * wanted / fullHeight;
                float newPosY = topEdge - wanted * 0.5f;

                Vector3 ls = p.T.localScale;
                p.T.localScale = new Vector3(ls.x, newLocalY, ls.z);
                Vector3 pos = p.T.position;
                p.T.position = new Vector3(pos.x, newPosY, pos.z);

                p.AppliedLocalY = newLocalY;
                p.AppliedPosY = newPosY;
            }
        }

        private static void AddPiece(Transform t)
        {
            if (t == null)
            {
                return;
            }
            Piece p = new Piece();
            p.T = t;
            p.FullLocalY = t.localScale.y;
            p.FullPosY = t.position.y;
            p.AppliedLocalY = float.NaN;     // nothing applied yet
            p.AppliedPosY = float.NaN;
            panel.Add(p);
        }

        /// <summary>
        /// Undoes the one change that outlives us: a halved row plate.
        ///
        /// Positions are deliberately NOT restored. The mapper pools its rows, so
        /// by the time this runs the containers we recorded may already have been
        /// rebuilt into some other block's mapper -- writing our remembered
        /// Top/Bottom onto those is what left the Cannon's rows strewn down its
        /// panel. Positions need no undo anyway: the mapper lays every row out
        /// itself on the next Rebuild, and Apply is idempotent.
        ///
        /// A width does need undoing, because nothing else sets it back. It is
        /// reverted only where the plate is still carrying the halved value, so a
        /// container already reused elsewhere is left alone.
        /// </summary>
        public static void Restore()
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
            if (scrollbar != null)
            {
                scrollbar.gameObject.SetActive(true);
            }
            scrollbar = null;
            panelOwner = null;
        }

        // ---- placement -------------------------------------------------------

        /// <summary>
        /// The output row order: non-mine rows keep their place and their order,
        /// and my rows go in as a block starting where the first of mine was.
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
        /// Halves two rows and sets them in two columns. Widths and offsets are the
        /// one thing done in local units, because Background.localScale is local.
        /// </summary>
        private static void SideBySide(ContainerDetails left, ContainerDetails right, float width, float centre)
        {
            Transform lbg = left.Background;
            Transform rbg = right.Background;
            if (lbg == null || rbg == null || width <= 0f)
            {
                return;                 // nothing measurable; leave them full width
            }

            float half = (width - width * ColumnGap) * 0.5f;
            float shift = (width - half) * 0.5f;

            RecordHalved(lbg, width, half);
            RecordHalved(rbg, width, half);

            Vector3 ls = lbg.localScale;
            lbg.localScale = new Vector3(half, ls.y, ls.z);
            Vector3 rs = rbg.localScale;
            rbg.localScale = new Vector3(half, rs.y, rs.z);

            // Both columns are placed against `centre`, which comes from a row this
            // never moves. Reading the moved row's own x instead shifts it again on
            // every pass, and since Apply runs every frame the columns march off the
            // panel within a second.
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
        /// Solves worldY = scale * localY + offset from two rows that sit at
        /// different heights. Measured because the rows' parent is scaled, and by
        /// how much is not a mod's to assume.
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


        // ---- diagnostics -----------------------------------------------------

        /// <summary>
        /// Logs what the mapper actually built: every row, what it belongs to, and
        /// every number this layout depends on. Kept because it is what turned this
        /// from guesswork into arithmetic. Greppable in Player.log.
        /// </summary>
        public static void Dump(BlockMapper mapper)
        {
            if (mapper == null)
            {
                Debug.Log("[SB layout] no mapper");
                return;
            }
            ContainerDetails[] all = mapper.GetComponentsInChildren<ContainerDetails>(true);
            if (all == null)
            {
                Debug.Log("[SB layout] no containers");
                return;
            }
            Debug.Log("[SB layout] ---- " + all.Length + " containers ----");
            for (int i = 0; i < all.Length; i++)
            {
                ContainerDetails c = all[i];
                if (c == null)
                {
                    continue;
                }
                string who = "(no selector)";
                if (c.selector != null && c.selector.MapperType != null)
                {
                    who = c.selector.MapperType.Key + " / " + c.selector.MapperType.DisplayName;
                }
                Vector3 lp = c.transform.localPosition;
                string bg = "none";
                if (c.Background != null)
                {
                    Vector3 bs = c.Background.localScale;
                    bg = "scale(" + bs.x + "," + bs.y + ")";
                }
                Transform p = c.transform.parent;
                Debug.Log("[SB layout] " + i
                    + " | " + who
                    + " | active=" + c.gameObject.activeInHierarchy
                    + " | local(" + lp.x + "," + lp.y + ")"
                    + " | worldY=" + c.transform.position.y
                    + " | Top=" + c.Top + " Bottom=" + c.Bottom + " H=" + c.Height
                    + " | bg " + bg
                    + " | parent=" + (p != null ? p.name : "null"));
            }
            Debug.Log("[SB layout] ---- end ----");
        }
    }
}

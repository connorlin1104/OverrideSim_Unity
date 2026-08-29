using UnityEngine;

// The look of the tile seams, as numbers. Every one is a fraction of a tile (or a count per edge),
// never a pixel or a world unit, so the same settings describe the same groove at any texture size
// and any field scale. The editor tool bakes these into the two seam textures; the marker below
// keeps the copy they were baked with, so the Tile Seams window opens on the values that are
// actually on the floor and a validator can regenerate the textures and prove the assets on disk
// still match what the scene says it has.
[System.Serializable]
public struct TileSeamSettings
{
    [Tooltip("Groove width as a fraction of one tile. 0.008 of a 598 mm tile is a 5 mm gap — what two foam tiles pressed together actually show.")]
    public float grooveWidth;
    [Tooltip("Tab-and-notch segments along one edge (a tab or a notch each). 12 makes each tab a twelfth of the tile wide, close to the real foam pattern. Always even: the pattern must come back to where it started by the far corner, or it would jump there.")]
    public int tabsPerEdge;
    [Tooltip("How far a tab reaches into the neighbouring tile, as a fraction of the tile. Only the half of the interlock inside THIS tile is drawn; the neighbour's texture completes it.")]
    public float tabDepth;
    [Tooltip("Albedo multiplier at the bottom of the groove (1 = invisible, 0 = black). The seam reads mostly through this darkening; the normal map only catches the light on the walls.")]
    public float grooveShade;
    [Tooltip("Normal-map strength: how steep the groove walls look. 3 tilts a wall about 45 degrees (the wall is the texture's fixed 3-texel anti-aliasing ramp, so the width setting does not change this); 0 flattens the normal map entirely.")]
    public float grooveDepth;

    public static TileSeamSettings Defaults => new TileSeamSettings
    {
        grooveWidth = 0.008f,
        tabsPerEdge = 12,
        tabDepth = 0.017f,
        grooveShade = 0.45f,
        grooveDepth = 3f,
    };

    // The slider ranges, kept with the numbers they bound. The window clamps to these, and so does
    // the tool, because a batch caller can hand it anything. Both tab bounds are even, see below.
    public const float MinGrooveWidth = 0.001f, MaxGrooveWidth = 0.04f;
    public const int MinTabsPerEdge = 2, MaxTabsPerEdge = 40;
    public const float MinTabDepth = 0f, MaxTabDepth = 0.06f;
    public const float MinGrooveShade = 0f, MaxGrooveShade = 1f;
    public const float MinGrooveDepth = 0f, MaxGrooveDepth = 8f;

    // The seam is a square wave that starts and ends each edge on the same level (the corners of
    // neighbouring tiles share one tab), so it needs a whole number of periods per edge — an even
    // count. An odd request rounds up rather than down so a slider dragged upward keeps moving.
    public static int EvenTabs(int tabsPerEdge)
    {
        int n = Mathf.Clamp(tabsPerEdge, MinTabsPerEdge, MaxTabsPerEdge);
        return n + (n & 1);
    }

    public TileSeamSettings Clamped()
    {
        TileSeamSettings s = this;
        s.grooveWidth = Mathf.Clamp(s.grooveWidth, MinGrooveWidth, MaxGrooveWidth);
        s.tabsPerEdge = EvenTabs(s.tabsPerEdge);
        s.tabDepth = Mathf.Clamp(s.tabDepth, MinTabDepth, MaxTabDepth);
        s.grooveShade = Mathf.Clamp(s.grooveShade, MinGrooveShade, MaxGrooveShade);
        s.grooveDepth = Mathf.Clamp(s.grooveDepth, MinGrooveDepth, MaxGrooveDepth);
        return s;
    }

    // Exact comparison on purpose: two settings that differ by a float ulp bake two different PNGs,
    // and "the textures on disk match the scene" is only true when they don't differ at all.
    public bool SameAs(TileSeamSettings other) =>
        grooveWidth == other.grooveWidth && tabsPerEdge == other.tabsPerEdge &&
        tabDepth == other.tabDepth && grooveShade == other.grooveShade && grooveDepth == other.grooveDepth;

    public override string ToString() =>
        $"grooveWidth {grooveWidth:0.####}, tabsPerEdge {tabsPerEdge}, tabDepth {tabDepth:0.####}, " +
        $"grooveShade {grooveShade:0.##}, grooveDepth {grooveDepth:0.##}";
}

// Sits on FloorTiles once Tile Seams has been applied, and does nothing at all at run time: no
// Update, no Awake, no physics. It is a receipt.
//
// What it holds is everything Remove needs to put the floor back exactly: which 36 renderers were
// touched, the FBX sub-mesh each one had before it was swapped for the generated slab, the one
// embedded material they all shared, and the static flags they carried. None of that is
// recoverable from the scene once the swap has happened — the original meshes are sub-assets
// inside OverrideFieldVersion3.fbx with generic names (Body1.315, Body1.294 ...), and nothing but
// this list says which tile had which.
//
// It also records the settings the seam textures were baked with. Two reasons: the Tile Seams
// window opens on what is actually on the floor rather than on the defaults, and the validator can
// regenerate the textures from these numbers and compare them pixel for pixel with the PNGs on
// disk — which is how a retune that reached SampleScene but not LiteScene gets caught.
[DisallowMultipleComponent]
public class FloorTileSeams : MonoBehaviour
{
    [Tooltip("The tile renderers the seams were applied to, in the order the arrays below use.")]
    public MeshRenderer[] tiles;
    [Tooltip("Per tile: the mesh it had before the slab swap (an OverrideFieldVersion3.fbx sub-mesh).")]
    public Mesh[] originalMeshes;
    [Tooltip("The one embedded FBX material every tile used before; the seam material was cloned from it.")]
    public Material originalMaterial;
    [Tooltip("Per tile: the GameObject's StaticEditorFlags before Batching Static was added.")]
    public int[] originalStaticFlags;
    [Tooltip("The numbers the seam textures on disk were generated from.")]
    public TileSeamSettings settings;
}

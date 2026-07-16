using UnityEngine;

// Project convention for identifying game pieces (cups/pins): name prefix. The field pieces were split
// from one FBX and carry no marker component, tag, or layer — a dedicated GamePiece marker/layer is the
// clean upgrade (claws/scoring will want one); this class is the single swap point for that convention.
// Used by IntakePull, PieceTargetProbe, GoalStackMagnet, and the game-piece editor tools.
public static class GamePiece
{
    public static bool IsPiece(GameObject go) => go != null && IsPiece(go.name);

    public static bool IsPiece(string name) =>
        !string.IsNullOrEmpty(name) && (name.StartsWith("Cup") || name.StartsWith("Pin"));
}

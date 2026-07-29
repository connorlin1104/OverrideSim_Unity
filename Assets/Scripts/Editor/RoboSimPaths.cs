// The asset paths the editor tools share, declared once. These were re-declared as private
// consts in over a dozen files under four different names, which is exactly how a moved scene
// breaks half the tools and not the other half.
internal static class RoboSimPaths
{
    // The full competition field scene — what the batch validators open and the scene tools edit.
    public const string MainScene = "Assets/Scenes/SampleScene.unity";

    // The RobotModelCatalog ScriptableObject listing every playable robot.
    public const string RobotModelCatalog = "Assets/Settings/RobotModelCatalog.asset";

    // Where the playable robot prefabs live.
    public const string RobotsFolder = "Assets/Robots";

    // The match-load piece prefabs the loaders spawn.
    public const string MatchLoadPrefabsFolder = "Assets/Models/MatchLoadPreFabs";
}

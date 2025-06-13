namespace Ovjo.OverdareStudio.AppInfo
{
    public abstract class AppInfo
    {
        public const UAssetAPI.UnrealTypes.EngineVersion OVERDARE_UNREAL_ENGINE_VERSION = UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_3;
        public abstract string DefaultTemplateUmapPath { get; }
    }
}

namespace Msync.Core;

public enum ArtifactStage : byte { Relocatable, Executable }
[Flags]
public enum ModuleSemantics : byte { Legacy = 0, MutableLocations = 1, DirectionalScheduling = 2, Current = MutableLocations | DirectionalScheduling }

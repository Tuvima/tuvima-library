namespace MediaEngine.Domain.PersonalMedia;

/// <summary>A face the face engine found in one photo (spec view-people-search WP7): where it is, how good it is, and its look as numbers.</summary>
public sealed record ViewDetectedFace(double X, double Y, double Width, double Height, double Quality, float[] Embedding);

/// <summary>A face ready to be saved, already put with a person (an existing one or one made for it).</summary>
public sealed record ViewFaceInsert(Guid FaceId, Guid PersonId, ViewDetectedFace Face);

/// <summary>A photo still waiting to be looked at for faces.</summary>
public sealed record ViewFaceScanCandidate(Guid ItemId, string FilePath);

/// <summary>One saved face with its look, used to work out which person a new face belongs with.</summary>
public sealed record ViewFaceLook(Guid FaceId, Guid? PersonId, float[] Embedding, bool Confirmed);

/// <summary>How far the face scan of one profile has got.</summary>
public sealed record ViewFaceScanProgress(int PhotosLooked, int PhotosTotal, int Faces, int People, int UnnamedGroups);

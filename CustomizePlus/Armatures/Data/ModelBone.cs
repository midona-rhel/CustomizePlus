using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CustomizePlus.Core.Data;
using CustomizePlus.Templates.Data;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;

namespace CustomizePlus.Armatures.Data;

/// <summary>
///     Represents a single bone of an ingame character's skeleton.
/// </summary>
public unsafe class ModelBone
{
    public enum PoseType
    {
        Local, Model, BindPose, World
    }

    public readonly Armature MasterArmature;

    public readonly int PartialSkeletonIndex;
    public readonly int BoneIndex;

    /// <summary>
    /// Gets the model bone corresponding to this model bone's parent, if it exists.
    /// (It should in all cases but the root of the skeleton)
    /// </summary>
    public ModelBone? ParentBone => _parentPartialIndex >= 0 && _parentBoneIndex >= 0
        ? MasterArmature[_parentPartialIndex, _parentBoneIndex]
        : null;
    private int _parentPartialIndex = -1;
    private int _parentBoneIndex = -1;

    /// <summary>
    /// Gets each model bone for which this model bone corresponds to a direct parent thereof.
    /// A model bone may have zero children.
    /// </summary>
    public IEnumerable<ModelBone> ChildBones => _childPartialIndices.Zip(_childBoneIndices, (x, y) => MasterArmature[x, y]);
    private List<int> _childPartialIndices = new();
    private List<int> _childBoneIndices = new();

    /// <summary>
    /// Gets the model bone that forms a mirror image of this model bone, if one exists.
    /// </summary>
    public ModelBone? TwinBone => _twinPartialIndex >= 0 && _twinBoneIndex >= 0
        ? MasterArmature[_twinPartialIndex, _twinBoneIndex]
        : null;
    private int _twinPartialIndex = -1;
    private int _twinBoneIndex = -1;

    /// <summary>
    /// The name of the bone within the in-game skeleton. Referred to in some places as its "code name".
    /// </summary>
    public string BoneName;

    /// <summary>
    /// The transform that this model bone will impart upon its in-game sibling when the master armature
    /// is applied to the in-game skeleton. Reference to transform contained in top most template in profile applied to character.
    /// </summary>
    public BoneTransform? CustomizedTransform { get; private set; }

    /// <summary>
    /// True if bone is linked to any template
    /// </summary>
    public bool IsActive => CustomizedTransform != null;

    public ModelBone(Armature arm, string codeName, int partialIdx, int boneIdx)
    {
        MasterArmature = arm;
        PartialSkeletonIndex = partialIdx;
        BoneIndex = boneIdx;

        BoneName = codeName;
    }

    /// <summary>
    /// Link bone to specific template, unlinks if null is passed
    /// </summary>
    /// <param name="template"></param>
    /// <returns></returns>
    public bool LinkToTemplate(Template? template)
    {
        if (template == null)
        {
            if (CustomizedTransform == null)
                return false;

            CustomizedTransform = null;

            Plugin.Logger.Verbose($"Unlinked {BoneName} from all templates");

            return true;
        }

        if (!template.Bones.ContainsKey(BoneName))
            return false;

        Plugin.Logger.Verbose($"Linking {BoneName} to {template.Name}");
        CustomizedTransform = template.Bones[BoneName];

        return true;
    }

    /// <summary>
    /// Indicate a bone to act as this model bone's "parent".
    /// </summary>
    public void AddParent(int parentPartialIdx, int parentBoneIdx)
    {
        if (_parentPartialIndex != -1 || _parentBoneIndex != -1)
        {
            throw new Exception($"Tried to add redundant parent to model bone -- {this}");
        }

        _parentPartialIndex = parentPartialIdx;
        _parentBoneIndex = parentBoneIdx;
    }

    /// <summary>
    /// Indicate that a bone is one of this model bone's "children".
    /// </summary>
    public void AddChild(int childPartialIdx, int childBoneIdx)
    {
        _childPartialIndices.Add(childPartialIdx);
        _childBoneIndices.Add(childBoneIdx);
    }

    /// <summary>
    /// Indicate a bone that acts as this model bone's mirror image, or "twin".
    /// </summary>
    public void AddTwin(int twinPartialIdx, int twinBoneIdx)
    {
        _twinPartialIndex = twinPartialIdx;
        _twinBoneIndex = twinBoneIdx;
    }

    public override string ToString()
    {
        //string numCopies = _copyIndices.Count > 0 ? $" ({_copyIndices.Count} copies)" : string.Empty;
        return $"{BoneName} ({BoneData.GetBoneDisplayName(BoneName)}) @ <{PartialSkeletonIndex}, {BoneIndex}>";
    }

    /// <summary>
    /// Get the lineage of this model bone, going back to the skeleton's root bone.
    /// </summary>
    public IEnumerable<ModelBone> GetAncestors(bool includeSelf = true) => includeSelf
        ? GetAncestors(new List<ModelBone>() { this })
        : GetAncestors(new List<ModelBone>());

    private IEnumerable<ModelBone> GetAncestors(List<ModelBone> tail)
    {
        tail.Add(this);
        if (ParentBone is ModelBone mb && mb != null)
        {
            return mb.GetAncestors(tail);
        }
        else
        {
            return tail;
        }
    }

    /// <summary>
    /// Gets all model bones with a lineage that contains this one.
    /// </summary>
    public IEnumerable<ModelBone> GetDescendants(bool includeSelf = false)
    {
        var descendants = new List<ModelBone>();
        if (includeSelf)
            descendants.Add(this);
            
        // First get direct children
        descendants.AddRange(ChildBones);
        
        // Then recursively get all of their children
        var index = 0;
        while (index < descendants.Count)
        {
            // Get children of the current bone and add them to the list
            var children = descendants[index].ChildBones.ToList();
            descendants.AddRange(children);
            index++;
        }
        
        return descendants;
    }

    /// <summary>
    /// Given a character base to which this model bone's master armature (presumably) applies,
    /// return the game's transform value for this model's in-game sibling within the given reference frame.
    /// </summary>
    public hkQsTransformf GetGameTransform(CharacterBase* cBase, PoseType refFrame)
    {

        var skelly = cBase->Skeleton;
        var pSkelly = skelly->PartialSkeletons[PartialSkeletonIndex];
        var targetPose = pSkelly.GetHavokPose(Constants.TruePoseIndex);
        //hkaPose* targetPose = cBase->Skeleton->PartialSkeletons[PartialSkeletonIndex].GetHavokPose(Constants.TruePoseIndex);

        if (targetPose == null) return Constants.NullTransform;

        return refFrame switch
        {
            PoseType.Local => targetPose->LocalPose[BoneIndex],
            PoseType.Model => targetPose->ModelPose[BoneIndex],
            _ => Constants.NullTransform
            //TODO properly implement the other options
        };
    }

    private void SetGameTransform(CharacterBase* cBase, hkQsTransformf transform, PoseType refFrame)
    {
        SetGameTransform(cBase, transform, PartialSkeletonIndex, BoneIndex, refFrame);
    }

    private static void SetGameTransform(CharacterBase* cBase, hkQsTransformf transform, int partialIndex, int boneIndex, PoseType refFrame)
    {
        var skelly = cBase->Skeleton;
        var pSkelly = skelly->PartialSkeletons[partialIndex];
        var targetPose = pSkelly.GetHavokPose(Constants.TruePoseIndex);
        //hkaPose* targetPose = cBase->Skeleton->PartialSkeletons[PartialSkeletonIndex].GetHavokPose(Constants.TruePoseIndex);

        if (targetPose == null || targetPose->ModelInSync == 0) return;

        switch (refFrame)
        {
            case PoseType.Local:
                targetPose->LocalPose.Data[boneIndex] = transform;
                return;

            case PoseType.Model:
                targetPose->ModelPose.Data[boneIndex] = transform;
                return;

            default:
                return;

                //TODO properly implement the other options
        }
    }

    /// <summary>
    /// Apply this model bone's associated transformation to its in-game sibling within
    /// the skeleton of the given character base.
    /// </summary>
    public void ApplyModelTransform(CharacterBase* cBase)
    {
        if (!IsActive)
            return;

        if (cBase != null
            && CustomizedTransform.IsEdited()
            && GetGameTransform(cBase, PoseType.Model) is hkQsTransformf gameTransform
            && !gameTransform.Equals(Constants.NullTransform)
            && CustomizedTransform.ModifyExistingTransform(gameTransform) is hkQsTransformf modTransform
            && !modTransform.Equals(Constants.NullTransform))
        {
            SetGameTransform(cBase, modTransform, PoseType.Model);
        }
    }

    public void ApplyModelScale(CharacterBase* cBase) => ApplyTransFunc(cBase, CustomizedTransform.ModifyExistingScale);
    public void ApplyModelRotation(CharacterBase* cBase) => ApplyTransFunc(cBase, CustomizedTransform.ModifyExistingRotation);
    public void ApplyModelFullTranslation(CharacterBase* cBase) => ApplyTransFunc(cBase, CustomizedTransform.ModifyExistingTranslationWithRotation);
    public void ApplyStraightModelTranslation(CharacterBase* cBase) => ApplyTransFunc(cBase, CustomizedTransform.ModifyExistingTranslation);
    
    /// <summary>
    /// Apply scale transformation to this bone and all bones in its hierarchy
    /// </summary>
    public void ApplyHierarchicalModelScale(CharacterBase* cBase)
    {
        if (!IsActive || CustomizedTransform == null || cBase == null)
            return;
            
        var scale = CustomizedTransform.HierarchicalScaling;
        if (scale.X == 1 && scale.Y == 1 && scale.Z == 1)
            return; // Skip processing if no scaling is applied
            
        Plugin.Logger.Debug($"Applying hierarchical scale {scale} to bone {BoneName}");
        
        // Get list of all bones in hierarchy in the correct order
        var allBones = new List<ModelBone>();
        GatherBoneHierarchy(allBones);
        
        Plugin.Logger.Debug($"Found {allBones.Count} bones in hierarchy starting from {BoneName}");
        
        // Create a set of bones we've already processed to avoid processing mirror bones twice
        var processedBones = new HashSet<(int, int)>();
        
        // Store original world positions of all bones
        var worldPositions = new Dictionary<(int, int), Vector3>();
        
        // Calculate original world positions first
        foreach (var bone in allBones)
        {
            var modelTransform = bone.GetGameTransform(cBase, PoseType.Model);
            if (!modelTransform.Equals(Constants.NullTransform))
            {
                worldPositions[(bone.PartialSkeletonIndex, bone.BoneIndex)] = 
                    new Vector3(modelTransform.Translation.X, modelTransform.Translation.Y, modelTransform.Translation.Z);
            }
        }
        
        // Process each bone in the hierarchy
        foreach (var bone in allBones)
        {
            // Skip if we've already processed this bone (as a mirror)
            var boneKey = (bone.PartialSkeletonIndex, bone.BoneIndex);
            if (processedBones.Contains(boneKey)) continue;
            
            // Add this bone to processed set
            processedBones.Add(boneKey);
            
            // Process the main bone
            var modelTransform = bone.GetGameTransform(cBase, PoseType.Model);
            if (!modelTransform.Equals(Constants.NullTransform))
            {
                // Scale the bone itself
                modelTransform.Scale.X *= scale.X;
                modelTransform.Scale.Y *= scale.Y;
                modelTransform.Scale.Z *= scale.Z;
                
                // Handle position update for non-root bones
                if (bone.ParentBone != null)
                {
                    var parent = bone.ParentBone;
                    var parentKey = (parent.PartialSkeletonIndex, parent.BoneIndex);
                    
                    if (worldPositions.ContainsKey(boneKey) && worldPositions.ContainsKey(parentKey))
                    {
                        // Calculate original offset from parent
                        var parentOldPos = worldPositions[parentKey];
                        var childOldPos = worldPositions[boneKey];
                        var oldOffset = childOldPos - parentOldPos;
                        
                        // Scale this offset
                        var newOffset = new Vector3(
                            oldOffset.X * scale.X,
                            oldOffset.Y * scale.Y,
                            oldOffset.Z * scale.Z
                        );
                        
                        // Get parent's current position
                        var parentTransform = parent.GetGameTransform(cBase, PoseType.Model);
                        if (!parentTransform.Equals(Constants.NullTransform))
                        {
                            var parentNewPos = new Vector3(
                                parentTransform.Translation.X,
                                parentTransform.Translation.Y,
                                parentTransform.Translation.Z
                            );
                            
                            // Calculate and set new position
                            var childNewPos = parentNewPos + newOffset;
                            modelTransform.Translation.X = childNewPos.X;
                            modelTransform.Translation.Y = childNewPos.Y;
                            modelTransform.Translation.Z = childNewPos.Z;
                        }
                    }
                }
                
                // Apply model transform directly
                var skelly = cBase->Skeleton;
                var pSkelly = skelly->PartialSkeletons[bone.PartialSkeletonIndex];
                var targetPose = pSkelly.GetHavokPose(Constants.TruePoseIndex);
                if (targetPose != null)
                {
                    targetPose->ModelPose.Data[bone.BoneIndex] = modelTransform;
                    Plugin.Logger.Verbose($"Applied scaling and moved {bone.BoneName}");
                    
                    // Update local transform for non-root bones
                    if (bone.ParentBone != null)
                    {
                        var parentModel = bone.ParentBone.GetGameTransform(cBase, PoseType.Model);
                        var localTransform = bone.GetGameTransform(cBase, PoseType.Local);
                        if (!localTransform.Equals(Constants.NullTransform) && !parentModel.Equals(Constants.NullTransform))
                        {
                            // Calculate local offset from parent's world position
                            var parentPos = new Vector3(
                                parentModel.Translation.X,
                                parentModel.Translation.Y,
                                parentModel.Translation.Z
                            );
                            var bonePos = new Vector3(
                                modelTransform.Translation.X,
                                modelTransform.Translation.Y,
                                modelTransform.Translation.Z
                            );
                            var worldOffset = bonePos - parentPos;
                            
                            // Update local translation
                            localTransform.Translation.X = worldOffset.X;
                            localTransform.Translation.Y = worldOffset.Y;
                            localTransform.Translation.Z = worldOffset.Z;
                            
                            // Apply local transform
                            targetPose->LocalPose.Data[bone.BoneIndex] = localTransform;
                        }
                    }
                }
            }
            
            // Process the mirror bone if it exists
            if (bone.TwinBone != null)
            {
                var twinBone = bone.TwinBone;
                var twinKey = (twinBone.PartialSkeletonIndex, twinBone.BoneIndex);
                
                // Add twin to processed set
                processedBones.Add(twinKey);
                
                var twinTransform = twinBone.GetGameTransform(cBase, PoseType.Model);
                if (!twinTransform.Equals(Constants.NullTransform))
                {
                    // Scale the twin bone
                    twinTransform.Scale.X *= scale.X;
                    twinTransform.Scale.Y *= scale.Y;
                    twinTransform.Scale.Z *= scale.Z;
                    
                    // Handle position update for non-root mirror bones
                    if (twinBone.ParentBone != null)
                    {
                        var twinParent = twinBone.ParentBone;
                        var twinParentKey = (twinParent.PartialSkeletonIndex, twinParent.BoneIndex);
                        
                        if (worldPositions.ContainsKey(twinKey) && worldPositions.ContainsKey(twinParentKey))
                        {
                            // Calculate original offset from parent
                            var twinParentOldPos = worldPositions[twinParentKey];
                            var twinOldPos = worldPositions[twinKey];
                            var twinOldOffset = twinOldPos - twinParentOldPos;
                            
                            // Scale this offset, mirroring X axis
                            var twinNewOffset = new Vector3(
                                twinOldOffset.X * scale.X,
                                twinOldOffset.Y * scale.Y,
                                twinOldOffset.Z * scale.Z
                            );
                            
                            // Get parent's current position
                            var twinParentTransform = twinParent.GetGameTransform(cBase, PoseType.Model);
                            if (!twinParentTransform.Equals(Constants.NullTransform))
                            {
                                var twinParentNewPos = new Vector3(
                                    twinParentTransform.Translation.X,
                                    twinParentTransform.Translation.Y,
                                    twinParentTransform.Translation.Z
                                );
                                
                                // Calculate and set new position
                                var twinNewPos = twinParentNewPos + twinNewOffset;
                                twinTransform.Translation.X = twinNewPos.X;
                                twinTransform.Translation.Y = twinNewPos.Y;
                                twinTransform.Translation.Z = twinNewPos.Z;
                            }
                        }
                    }
                    
                    // Apply twin model transform
                    var twinSkelly = cBase->Skeleton->PartialSkeletons[twinBone.PartialSkeletonIndex];
                    var twinPose = twinSkelly.GetHavokPose(Constants.TruePoseIndex);
                    if (twinPose != null)
                    {
                        twinPose->ModelPose.Data[twinBone.BoneIndex] = twinTransform;
                        Plugin.Logger.Verbose($"Applied scaling and moved mirror bone {twinBone.BoneName}");
                        
                        // Update local transform for non-root mirror bones
                        if (twinBone.ParentBone != null)
                        {
                            var twinParentModel = twinBone.ParentBone.GetGameTransform(cBase, PoseType.Model);
                            var twinLocalTransform = twinBone.GetGameTransform(cBase, PoseType.Local);
                            if (!twinLocalTransform.Equals(Constants.NullTransform) && !twinParentModel.Equals(Constants.NullTransform))
                            {
                                // Calculate local offset from parent's world position
                                var twinParentPos = new Vector3(
                                    twinParentModel.Translation.X,
                                    twinParentModel.Translation.Y,
                                    twinParentModel.Translation.Z
                                );
                                var twinPos = new Vector3(
                                    twinTransform.Translation.X,
                                    twinTransform.Translation.Y,
                                    twinTransform.Translation.Z
                                );
                                var twinWorldOffset = twinPos - twinParentPos;
                                
                                // Update local translation
                                twinLocalTransform.Translation.X = twinWorldOffset.X;
                                twinLocalTransform.Translation.Y = twinWorldOffset.Y;
                                twinLocalTransform.Translation.Z = twinWorldOffset.Z;
                                
                                // Apply local transform
                                twinPose->LocalPose.Data[twinBone.BoneIndex] = twinLocalTransform;
                            }
                        }
                    }
                }
            }
        }
        
        // Force update of all poses
        foreach (var partialIdx in Enumerable.Range(0, cBase->Skeleton->PartialSkeletonCount))
        {
            var pSkelly = cBase->Skeleton->PartialSkeletons[partialIdx];
            var pose = pSkelly.GetHavokPose(Constants.TruePoseIndex);
            if (pose != null)
            {
                pose->ModelInSync = 0;
            }
        }
    }
    
    /// <summary>
    /// Gathers this bone and all descendant bones in hierarchy order
    /// </summary>
    private void GatherBoneHierarchy(List<ModelBone> boneList)
    {
        // Add self first
        if (!boneList.Contains(this))
        {
            boneList.Add(this);
            Plugin.Logger.Verbose($"Added bone {BoneName} to hierarchy");
        }
        
        // Then add each child and their descendants
        foreach (var child in ChildBones)
        {
            if (child != null && !boneList.Contains(child))
            {
                Plugin.Logger.Verbose($"Processing child {child.BoneName} of {BoneName}");
                child.GatherBoneHierarchy(boneList);
            }
        }
    }

    private void ApplyTransFunc(CharacterBase* cBase, Func<hkQsTransformf, hkQsTransformf> modTrans)
    {
        if (!IsActive)
            return;

        if (cBase != null
            && CustomizedTransform.IsEdited()
            && GetGameTransform(cBase, PoseType.Model) is hkQsTransformf gameTransform
            && !gameTransform.Equals(Constants.NullTransform))
        {
            var modTransform = modTrans(gameTransform);

            if (!modTransform.Equals(gameTransform) && !modTransform.Equals(Constants.NullTransform))
            {
                SetGameTransform(cBase, modTransform, PoseType.Model);
            }
        }
    }

    /// <summary>
    /// Checks for a non-zero and non-identity (root) scale.
    /// </summary>
    /// <param name="mb">The bone to check</param>
    /// <returns>If the scale should be applied.</returns>
    public bool IsModifiedScale()
    {
        if (!IsActive)
            return false;
        return CustomizedTransform.Scaling.X != 0 && CustomizedTransform.Scaling.X != 1 ||
               CustomizedTransform.Scaling.Y != 0 && CustomizedTransform.Scaling.Y != 1 ||
               CustomizedTransform.Scaling.Z != 0 && CustomizedTransform.Scaling.Z != 1;
    }
}
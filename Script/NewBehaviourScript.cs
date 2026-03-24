using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class AvatarMaker
{
    private static readonly List<string> HUMAN_NAMES = new List<string>()
    {
        "Head",
        "Hips",
        "LeftFoot",
        "LeftHand",
        "LeftLowerArm",
        "LeftLowerLeg",
        "LeftUpperArm",
        "LeftUpperLeg",
        "RightFoot",
        "RightHand",
        "RightLowerArm",
        "RightLowerLeg",
        "RightUpperArm",
        "RightUpperLeg",
        "Spine",
    };

    private static readonly int RECURSION_DEPTH_LIMIT = 50;

    [MenuItem("CustomTools/MakeHumanAvatar")]
    private static void MakeHumanAvatar()
    {
        GameObject activeGameObject = Selection.activeGameObject;

        if (activeGameObject != null)
        {
            var numHumanBones = HUMAN_NAMES.Count;
            HumanBone[] humanBones = new HumanBone[numHumanBones];
            for (var i = 0; i < numHumanBones; i++)
            {
                var humanName = HUMAN_NAMES[i];
                humanBones[i] = new HumanBone
                {
                    boneName = humanName,
                    humanName = humanName
                };
                humanBones[i].limit.useDefaultValues = true;
            }

            var skeletonBones = new SkeletonBone[numHumanBones + 1];
            skeletonBones[0] = new SkeletonBone
            {
                name = "Root",
                position = Vector3.zero,
                rotation = Quaternion.identity,
                scale = Vector3.one
            };

            for (var i = 0; i < numHumanBones; i++)
            {
                var humanName = HUMAN_NAMES[i];
                BoneOffsetFromRoot(
                    activeGameObject,
                    humanName,
                    out var position,
                    out var rotation,
                    out var scale
                );

                skeletonBones[i + 1] = new SkeletonBone
                {
                    name = humanName,
                    position = position,
                    rotation = rotation,
                    scale = scale
                };
            }

            var humanDescription = new HumanDescription
            {
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.5f,
                legStretch = 0.5f,
                feetSpacing = 0.0f,
                hasTranslationDoF = false,
                human = humanBones,
                skeleton = skeletonBones
            };

            Avatar avatar = AvatarBuilder.BuildHumanAvatar(activeGameObject, humanDescription);
            avatar.name = activeGameObject.name;
            Debug.Log(avatar.isHuman ? "is human" : "is generic");

            var path = string.Format("Assets/{0}.ht", avatar.name.Replace(':', '_'));
            AssetDatabase.CreateAsset(avatar, path);
        }
    }

    private static void BoneOffsetFromRoot(
        GameObject rootGameObject,
        string boneName,
        out Vector3 position,
        out Quaternion rotation,
        out Vector3 scale
    )
    {
        Debug.Log(boneName);
        var boneTransform = RecursiveFindChild(rootGameObject.transform,boneName);
        var boneLocalPosition = boneTransform.transform.localPosition;
        var boneLocalRotation = boneTransform.transform.localRotation;
        var boneLocalScale = boneTransform.transform.localScale;


        GameObjectOffsetFrom(
            rootGameObject.transform,
            boneTransform,
            in boneLocalPosition,
            in boneLocalRotation,
            in boneLocalScale,
            out position,
            out rotation,
            out scale,
            0
        );
    }

    private static void GameObjectOffsetFrom(
        Transform ancestorTransform,
        Transform gameObjectTransform,
        in Vector3 inputPosition,
        in Quaternion inputRotation,
        in Vector3 inputScale,
        out Vector3 position,
        out Quaternion rotation,
        out Vector3 scale,
        int recursionDepth
    )
    {
        if(recursionDepth > RECURSION_DEPTH_LIMIT)
        {
            throw new UnityException(
                "Making a human avatar failed due to incorrect" +
                " structure of the GameObject's hierarchy"
            );
        }

        if(ancestorTransform == gameObjectTransform)
        {
            position = inputPosition;
            rotation = inputRotation;
            scale = inputScale;
            return;
        }
        else
        {
            var parentTransform = gameObjectTransform.parent;
            var newPosition = inputPosition + parentTransform.localPosition;
            var newRotation = inputRotation * parentTransform.localRotation;
            var newScale = inputScale;
            newScale.Scale(parentTransform.localScale);
            GameObjectOffsetFrom(
                ancestorTransform,
                parentTransform,
                in newPosition,
                in newRotation,
                in newScale,
                out position,
                out rotation,
                out scale,
                recursionDepth
            );
            return;
        }
    }

    private static Transform RecursiveFindChild(Transform parent, string childName)
    {
        foreach (Transform child in parent)
        {
            if (child.name.Equals(childName))
            {
                return child;
            }
            else
            {
                Transform found = RecursiveFindChild(child, childName);
                if (found != null)
                {
                    return found;
                }
            }
        }
        return null;
    }
}
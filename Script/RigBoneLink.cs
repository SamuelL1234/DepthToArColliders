using UnityEngine;

[System.Serializable]
public class RigBoneLink
{
    public string name;

    public Transform animationBone;
    public Transform physicsBone;

    public Quaternion initialOffset;

    public void Initialize()
    {
        if (animationBone == null || physicsBone == null)
        {
            Debug.LogError($"BoneLink {name} is missing a transform.");
            return;
        }

        initialOffset =
            Quaternion.Inverse(animationBone.localRotation) *
            physicsBone.localRotation;
    }

    public Quaternion GetTargetRotation()
    {
        return animationBone.localRotation * initialOffset;
    }
}
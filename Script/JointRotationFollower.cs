using UnityEngine;

[RequireComponent(typeof(ConfigurableJoint))]
public class JointRotationFollower : MonoBehaviour
{
    public Transform animationBone;
    public Transform animationParent;

    ConfigurableJoint joint;

    Quaternion initialLocalRotation;

    void Awake()
    {
        joint = GetComponent<ConfigurableJoint>();

        if (animationBone == null || animationParent == null)
        {
            Debug.LogError("JointRotationFollower missing animation references.");
        }

        initialLocalRotation = transform.localRotation;
    }

    void FixedUpdate()
    {
        if (animationBone == null || animationParent == null)
            return;

        Quaternion animLocal =
            Quaternion.Inverse(animationParent.rotation) *
            animationBone.rotation;

        joint.targetRotation =
            Quaternion.Inverse(animLocal) *
            initialLocalRotation;
    }
}
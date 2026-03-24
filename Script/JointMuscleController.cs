using UnityEngine;

[RequireComponent(typeof(ConfigurableJoint))]
[RequireComponent(typeof(JointRotationFollower))]
public class JointMuscleController : MonoBehaviour
{
    public float baseSpring = 8000f;
    public float maxSpring = 25000f;

    public float damper = 250f;

    ConfigurableJoint joint;
    JointRotationFollower follower;

    void Awake()
    {
        joint = GetComponent<ConfigurableJoint>();
        follower = GetComponent<JointRotationFollower>();
    }

    void FixedUpdate()
    {
        Quaternion current = transform.localRotation;
        Quaternion target = follower.animationBone.localRotation;

        float angle;
        Vector3 axis;

        Quaternion delta = Quaternion.Inverse(current) * target;
        delta.ToAngleAxis(out angle, out axis);

        if (angle > 180f) angle -= 360f;

        float error = Mathf.Abs(angle) / 180f;

        float spring = Mathf.Lerp(baseSpring, maxSpring, error);

        JointDrive drive = joint.slerpDrive;

        drive.positionSpring = spring;
        drive.positionDamper = damper;
        drive.maximumForce = Mathf.Infinity;

        joint.slerpDrive = drive;
    }
}
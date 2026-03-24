using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class TorsoStabilizer : MonoBehaviour
{
    public Transform animationHips;

    public float uprightTorque = 800f;
    public float damping = 50f;

    Rigidbody rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    void FixedUpdate()
    {
        if (animationHips == null)
            return;

        Vector3 currentUp = transform.up;
        Vector3 targetUp = animationHips.up;

        Vector3 torque = Vector3.Cross(currentUp, targetUp);

        rb.AddTorque(torque * uprightTorque - rb.angularVelocity * damping);
    }
}
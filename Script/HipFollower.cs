using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class HipFollower : MonoBehaviour
{
    public Transform animationHips;

    Rigidbody rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        if (animationHips == null)
        {
            Debug.LogError("HipFollower: Animation hips not assigned.");
        }
    }

    void FixedUpdate()
    {
        if (animationHips == null)
            return;

        // Hard lock to animation pose
        transform.position = animationHips.position;
        transform.rotation = animationHips.rotation;

        // Prevent physics drift
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }
}
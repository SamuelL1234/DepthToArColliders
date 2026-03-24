using UnityEngine;

public class CameraController : MonoBehaviour
{
    [Header("Mouse Look")]
    public float sensitivity = 2f;
    public float minPitch = -89f;
    public float maxPitch = 89f;

    [Header("Movement")]
    public float moveSpeed = 10f;
    public float boostMultiplier = 3f;

    float yaw;
    float pitch;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        yaw = transform.eulerAngles.y;
        pitch = transform.eulerAngles.x;
    }

    void Update()
    {
        Look();
        Move();
    }

    void Look()
    {
        float mouseX = Input.GetAxisRaw("Mouse X") * sensitivity;
        float mouseY = Input.GetAxisRaw("Mouse Y") * sensitivity;

        yaw += mouseX;
        pitch -= mouseY;

        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    void Move()
    {
        float speed = moveSpeed;

        if (Input.GetKey(KeyCode.LeftShift))
            speed *= boostMultiplier;

        Vector3 move =
            transform.forward * Input.GetAxisRaw("Vertical") +
            transform.right * Input.GetAxisRaw("Horizontal");

        if (Input.GetKey(KeyCode.E))
            move += Vector3.up;

        if (Input.GetKey(KeyCode.Q))
            move += Vector3.down;

        transform.position += move * speed * Time.deltaTime;
    }
}
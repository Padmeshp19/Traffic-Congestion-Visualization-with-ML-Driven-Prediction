using UnityEngine;

public class FreeCameraController : MonoBehaviour
{
    public float speed = 10f;
    public float mouseSensitivity = 2f;

    float rotationX = 0f;
    float rotationY = 0f;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
    }

    void Update()
    {
        // Mouse Look
        rotationX += Input.GetAxis("Mouse X") * mouseSensitivity * 100f * Time.deltaTime;
        rotationY -= Input.GetAxis("Mouse Y") * mouseSensitivity * 100f * Time.deltaTime;
        rotationY = Mathf.Clamp(rotationY, -90f, 90f);

        transform.rotation = Quaternion.Euler(rotationY, rotationX, 0f);

        // Movement
        float moveX = Input.GetAxis("Horizontal");
        float moveZ = Input.GetAxis("Vertical");

        transform.position += transform.forward * moveZ * speed * Time.deltaTime;
        transform.position += transform.right * moveX * speed * Time.deltaTime;
    }
}

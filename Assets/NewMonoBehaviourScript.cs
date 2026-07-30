using RPGDemo.GameFramework;
using UnityEngine;

public class StandaloneGameBootstrap : MonoBehaviour
{
    [SerializeField]
    private PlayerController playerController;

    [SerializeField]
    private Character character;

    private void Start()
    {
        if (playerController == null || character == null)
        {
            Debug.LogError(
                "PlayerController ªÚ Character √ª”–≈‰÷√°£",
                this);
            return;
        }

        PossessionResult result =
            playerController.Possess(character);

        Debug.Log($"Possess result: {result}", this);
    }
}
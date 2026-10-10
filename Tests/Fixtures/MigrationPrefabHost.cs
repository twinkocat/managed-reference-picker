using UnityEngine;

namespace ManagedReferencePicker.Tests
{
    public sealed class MigrationPrefabHost : MonoBehaviour
    {
        [SerializeReference] public MigrationNode node;
    }
}

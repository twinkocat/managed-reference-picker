using System;
using System.Collections.Generic;
using UnityEngine;

namespace ManagedReferencePicker.Tests
{
    public sealed class MigrationAssetHost : ScriptableObject
    {
        [SerializeReference] public MigrationNode primary;
        [SerializeReference] public MigrationNode shared;
        [SerializeReference] public List<MigrationNode> nodes = new();
    }

    [Serializable]
    public sealed class MigrationNode
    {
        public int value;
        public string text;
        public UnityEngine.Object target;
        [SerializeReference] public MigrationNode next;
    }
}

using UnityEngine;

namespace RPGDemo.GameFramework
{
    public class PlayerState : MonoBehaviour
    {
        private Controller owningController;
        private Pawn pawn;

        public Controller OwningController => owningController;
        public Pawn Pawn => pawn;

        internal void SetOwningController(Controller controller)
        {
            owningController = controller;
        }

        internal void SetPawn(Pawn pawn)
        {
            this.pawn = pawn;
        }
    }
}

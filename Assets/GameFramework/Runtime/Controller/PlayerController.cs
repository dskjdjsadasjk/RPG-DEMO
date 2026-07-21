namespace RPGDemo.GameFramework
{
    public class PlayerController : Controller
    {
        protected virtual void Awake()
        {
            SetIsPlayerController(true);
        }

        public override bool IsLocalController => true;

        protected override void OnPossess(Pawn inPawn)
        {
            base.OnPossess(inPawn);

            if (Pawn == inPawn && Pawn != null)
            {
                ChangeState(ControllerStates.Playing);
            }
        }

        protected override void OnUnPossess()
        {
            base.OnUnPossess();
        }
    }
}

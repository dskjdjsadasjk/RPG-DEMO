namespace RPGDemo.GameFramework
{
    public class AIController : Controller
    {
        public override bool IsLocalController => true;

        protected override void OnPossess(Pawn inPawn)
        {
            base.OnPossess(inPawn);
        }

        protected override void OnUnPossess()
        {
            base.OnUnPossess();
        }
    }
}

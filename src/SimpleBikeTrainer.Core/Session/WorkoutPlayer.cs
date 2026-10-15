using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Session;

/// <summary>Steps through a workout one riding second at a time.</summary>
public sealed class WorkoutPlayer
{
    private readonly Workout _workout;

    public WorkoutPlayer(Workout workout)
    {
        _workout = workout;
        SkipEmptySteps();
    }

    public int StepIndex { get; private set; }

    public int StepElapsed { get; private set; }

    public bool IsFinished => StepIndex >= _workout.Steps.Count;

    public WorkoutStep? Current => IsFinished ? null : _workout.Steps[StepIndex];

    public WorkoutStep? Next => StepIndex + 1 < _workout.Steps.Count ? _workout.Steps[StepIndex + 1] : null;

    public int StepRemaining => Current is { } step ? Math.Max(0, step.DurationSeconds - StepElapsed) : 0;

    /// <summary>How far through the current step, from 0 to 1, for ramps.</summary>
    public double StepFraction => Current is { DurationSeconds: > 0 } step ? (double)StepElapsed / step.DurationSeconds : 0;

    /// <summary>Riding time into the workout, counting skipped steps as done.</summary>
    public long WorkoutElapsed =>
        _workout.Steps.Take(StepIndex).Sum(s => (long)s.DurationSeconds) + StepElapsed;

    /// <summary>Advances one second. Returns true when that second finished the step.</summary>
    public bool Tick()
    {
        if (IsFinished)
        {
            return false;
        }

        StepElapsed++;
        if (StepElapsed < Current!.DurationSeconds)
        {
            return false;
        }

        MoveTo(StepIndex + 1);
        return true;
    }

    public void Skip()
    {
        if (!IsFinished)
        {
            MoveTo(StepIndex + 1);
        }
    }

    public void JumpTo(int index) => MoveTo(Math.Clamp(index, 0, _workout.Steps.Count));

    private void MoveTo(int index)
    {
        StepIndex = index;
        StepElapsed = 0;
        SkipEmptySteps();
    }

    private void SkipEmptySteps()
    {
        while (!IsFinished && _workout.Steps[StepIndex].DurationSeconds <= 0)
        {
            StepIndex++;
        }
    }
}

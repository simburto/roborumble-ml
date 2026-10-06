using System;

namespace RoboRumble.Training
{
    // A compact, state-based policy. Parameters are learned from full-match scores.
    [Serializable]
    public class PolicyModel
    {
        public int version = 38;
        public string scene = "2022 Singleplayer";
        public int team = 0;
        public int robot = 1678;
        public double trainingScore = -1;
        public float[] weights = { .2f, 2f, 6f, .2f, .1f, .2f, 12f, 1f, .25f, 3f, 3f, .68f, .7f, 40f, 100f, .8f, 1.5f, 1f, 1f, 1f, 1f, 0f };
        public static readonly float[] Lower = { .08f, 1f, .5f, 0f, 0f, 0f, 1f, .25f, 0f, 0f, .5f, .64f, 0f, 20f, 60f, 0f, .5f, 0f, 0f, 0f, 0f, 0f };
        public static readonly float[] Upper = { .7f, 2f, 12f, .8f, 3f, 2f, 20f, 1f, .8f, 30f, 6f, .70f, 1.5f, 45f, 160f, 1.5f, 2.5f, 3f, 2f, 2f, 1f, 1f };

        public void Validate()
        {
            if (version != 38 || weights == null || weights.Length != Lower.Length)
                throw new ArgumentException("Unsupported or malformed policy model.");
            if (scene != "2022 Singleplayer" || team != 0)
                throw new ArgumentException("This trainer evaluates the red robot in 2022 Singleplayer.");
            if (robot != 254 && robot != 1678 && robot != 1323) throw new ArgumentException("Unsupported training robot.");
            for (int i = 0; i < weights.Length; i++)
                if (float.IsNaN(weights[i]) || float.IsInfinity(weights[i]) || weights[i] < Lower[i] || weights[i] > Upper[i])
                    throw new ArgumentException("Policy weight outside bounds: " + i);
        }
    }

    [Serializable]
    public class SearchState
    {
        public int version = 38;
        public int generation;
        public int seed = 42;
        public int population = 24;
        public int episodes = 3;
        public int robot = 1678;
        public float[] mean = (float[])new PolicyModel().weights.Clone();
        public float[] deviation = { .15f, .3f, 2f, .2f, .8f, .5f, 4f, .2f, .2f, 4f, 1.5f, .1f, .4f, 8f, 25f, .4f, .5f, .5f, .5f, .5f, .3f, .3f };
        public PolicyModel best = new PolicyModel();

        public PolicyModel[] Sample()
        {
            Validate();
            var random = new Random(unchecked(seed + generation * 7919));
            var models = new PolicyModel[population];
            for (int n = 0; n < models.Length; n++)
            {
                models[n] = new PolicyModel { robot = robot };
                for (int d = 0; d < mean.Length; d++)
                {
                    double gaussian = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
                    float v = n == 0 ? mean[d] : n == 1 && best.trainingScore >= 0 ? best.weights[d] : mean[d] + deviation[d] * (float)gaussian;
                    models[n].weights[d] = Math.Max(PolicyModel.Lower[d], Math.Min(PolicyModel.Upper[d], v));
                }
            }
            return models;
        }

        public void Validate()
        {
            if (version != 38 || generation < 0 || population < 4 || episodes < 1 ||
                mean == null || deviation == null || mean.Length != PolicyModel.Lower.Length || deviation.Length != PolicyModel.Lower.Length || best == null)
                throw new ArgumentException("Malformed training checkpoint.");
            best.Validate();
            if (best.robot != robot) throw new ArgumentException("Checkpoint robot mismatch.");
            for (int i = 0; i < mean.Length; i++)
                if (float.IsNaN(mean[i]) || float.IsInfinity(mean[i]) || mean[i] < PolicyModel.Lower[i] || mean[i] > PolicyModel.Upper[i] ||
                    float.IsNaN(deviation[i]) || float.IsInfinity(deviation[i]) || deviation[i] <= 0)
                    throw new ArgumentException("Invalid search distribution.");
        }

        public void Update(PolicyModel[] models, double[] scores)
        {
            if (models.Length != population || scores.Length != population) throw new ArgumentException("Population size mismatch.");
            var order = new int[population];
            for (int i = 0; i < population; i++)
            {
                models[i].Validate();
                if (double.IsNaN(scores[i]) || double.IsInfinity(scores[i])) throw new ArgumentException("Non-finite score.");
                order[i] = i;
            }
            Array.Sort(order, (a, b) => scores[b].CompareTo(scores[a]));
            // Slot 1 reevaluates the incumbent. Compare this generation's measurements:
            // PhysX outcomes can vary even on a common seed, so a lucky historical
            // score must not prevent a genuinely better current candidate replacing it.
            // The runner saves the all-time match record separately.
            best = models[order[0]];
            best.trainingScore = scores[order[0]];
            int elite = Math.Max(2, population / 4);
            for (int d = 0; d < mean.Length; d++)
            {
                float average = 0;
                for (int i = 0; i < elite; i++) average += models[order[i]].weights[d] / elite;
                float variance = 0;
                for (int i = 0; i < elite; i++)
                {
                    float delta = models[order[i]].weights[d] - average;
                    variance += delta * delta / elite;
                }
                mean[d] = .3f * mean[d] + .7f * average;
                deviation[d] = Math.Max((PolicyModel.Upper[d] - PolicyModel.Lower[d]) * .04f,
                    .3f * deviation[d] + .7f * (float)Math.Sqrt(variance));
            }
            generation++;
        }
    }
}

















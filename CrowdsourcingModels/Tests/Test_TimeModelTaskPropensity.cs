﻿//#define usePvlScore

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MicrosoftResearch.Infer;
using MicrosoftResearch.Infer.Distributions;
using MicrosoftResearch.Infer.Maths;
using MicrosoftResearch.Infer.Models;
using MicrosoftResearch.Infer.Utils;

namespace CrowdsourcingModels
{
    public class Test_TimeModelTaskPropensity
    {
        private static int[][] GenerateMultimodeTimeData(
            int workerCount,
            int taskCount,
            out int[][] workerLabels,
            ref double[][] timeSpent,
            double initialWorkerBelief = 5.5,
            int labelCount = 3)
        {
            // Generate true labels
            Vector backgroundLabelProb = Dirichlet.Symmetric(labelCount, 10.0).Sample();
            Discrete trueLabelDistrib = new Discrete(backgroundLabelProb);
            int[] trueLabel = Util.ArrayInit<int>(taskCount, i => trueLabelDistrib.Sample());
            Random random = new Random();

            Console.WriteLine("***Ground truth task label***");
            for (int i = 0; i < taskCount; i++)
            {
                Console.WriteLine("Task {0}: {1}", i, trueLabel[i]);
            }

            // Generate worker confusion matrix
            Dirichlet[] confusionMatrixPrior = new Dirichlet[labelCount];
            for (int d = 0; d < labelCount; d++)
            {
                confusionMatrixPrior[d] = new Dirichlet(Util.ArrayInit(labelCount, i => i == d ? (initialWorkerBelief / (1.0 - initialWorkerBelief)) * (labelCount - 1) : 1.0));
            }

            Vector[][] workerConfusionMatrix = Util.ArrayInit(
                workerCount, k => Util.ArrayInit(
                    labelCount, d => confusionMatrixPrior[d].Sample()));

            using (StreamWriter writer = new StreamWriter(Console.OpenStandardOutput()))
            {
                writer.WriteLine("\n*** Ground truth worker confusion matrices ***");
                for (int k = 0; k < Math.Min(workerCount, 2); k++)
                {
                    Results.WriteWorkerConfusionMatrix(writer, k.ToString(), workerConfusionMatrix[k]);
                }
            }

            // Generate task time range
            double[] taskShortTime = Util.ArrayInit<double>(taskCount, i => Gaussian.Sample(20,1));
            double[] taskLongTime = Util.ArrayInit<double>(taskCount, i => Gaussian.Sample(100,1));
            Console.WriteLine("\n***Ground truth time distributions***");
            for (int i = 0; i < taskCount; i++)
            {
                Console.WriteLine("Task {0}: Short Time {1:0.000}, Long Time {2:0.000}", i, taskShortTime[i], taskLongTime[i]);
            }

            // Generate spammer label probs
            Dirichlet dirUnifLabels = Dirichlet.Uniform(labelCount);
            Vector spammerLabelProb = dirUnifLabels.Sample();
            Dirichlet dirUnifSpammerTypes = Dirichlet.Uniform(3);

            Console.WriteLine("\n*** Ground truth spammer label prob ***");
            Console.WriteLine("{0}", spammerLabelProb);

#if usePvlScore
            double backgroundScore = 0.8;
            Console.WriteLine("\n***Ground truth background propensity***");
            Console.WriteLine("Background propensity score {0:0.000}", backgroundScore);

            double[] propensityForValidLabellingScore = Util.ArrayInit(workerCount, i => Gaussian.Sample(backgroundScore, 10));

            Console.WriteLine("\n***Ground truth propensity***");
            for (int k = 0; k < workerCount; k++)
            {
                Console.WriteLine("Worker {0}: Propensity score {1:0.000}", k, propensityForValidLabellingScore[k]);
            }
#else
            // Generate worker propensity
            double[] propensityForValidLabelling = Util.ArrayInit(workerCount, i => Beta.Sample(5, 1));
            Console.WriteLine("\n***Ground truth propensity***");
            for (int k = 0; k < workerCount; k++)
            {
                Console.WriteLine("Worker {0}: Propensity {1:0.000}", k, propensityForValidLabelling[k]);
            }
#endif

            // Generate worker labels
            workerLabels = new int[workerCount][];
            int[][] taskIndex = new int[workerCount][];
            timeSpent = new double[workerCount][];

            // Generate isValidLabellingAttempt matrix
            bool[][] isValidLabellingAttempt = new bool[workerCount][];
            int numJudgmentsPerWorker = 1000;
            for (int k = 0; k < workerCount; k++)
            {

                // Assume that workers can judge a task more than once
                taskIndex[k] = Util.ArrayInit<int>(numJudgmentsPerWorker, i => Rand.Int(0, taskCount));

#if usePvlScore
                isValidLabellingAttempt[k] = Util.ArrayInit<bool>(numJudgmentsPerWorker, i => Gaussian.Sample(propensityForValidLabellingScore[k], 100) > 0);
#else
                isValidLabellingAttempt[k] = Util.ArrayInit<bool>(numJudgmentsPerWorker, i => Bernoulli.Sample(propensityForValidLabelling[k]));
#endif

                workerLabels[k] = Util.ArrayInit<int>(taskIndex[k].Length, i => 0);
                timeSpent[k] = Util.ArrayInit<double>(taskIndex[k].Length, i => 0.0);

                for (int kn = 0; kn < taskIndex[k].Length; kn++)
                {

                    if (isValidLabellingAttempt[k][kn])
                    {
                        workerLabels[k][kn] = Discrete.Sample(workerConfusionMatrix[k][trueLabel[taskIndex[k][kn]]]);
                        double taskTimeRange = taskLongTime[taskIndex[k][kn]] - taskShortTime[taskIndex[k][kn]];
                        timeSpent[k][kn] = random.NextDouble() * taskTimeRange + taskShortTime[taskIndex[k][kn]];
                    }
                    else
                    {
                        workerLabels[k][kn] = Discrete.Sample(spammerLabelProb);
                        timeSpent[k][kn] = random.NextDouble() * (120 - taskLongTime[taskIndex[k][kn]]) + taskLongTime[taskIndex[k][kn]];
                    }
                }
            }

            return taskIndex;
        }

        public static void TestBCCTimeTaskMultimode(int workerCount, int taskCount, int labelCount = 3, double initialWorkerBelief = 0.7)
        {

            int[][] taskIndex = null;
            int[][] workerLabels = null;
            double[][] timeSpent = null;
            taskIndex = GenerateMultimodeTimeData(workerCount, taskCount, out workerLabels, ref timeSpent, initialWorkerBelief, labelCount);

            BCCTimeTaskPropensity model = new BCCTimeTaskPropensity();
            model.CreateModel(taskCount, labelCount);
            model.NumberOfIterations = 20;
            BCCTimeTaskPropensityPosteriors result = model.Infer(taskIndex, workerLabels, timeSpent, taskCount, true);

            Console.WriteLine("\n***Inferred task label***");
            for (int i = 0; i < taskCount; i++)
            {
                Console.WriteLine("Task {0}: {1}, Mode: {2}", i, result.TrueLabel[i], result.TrueLabel[i].GetMode());
            }

            Console.WriteLine("\n***Inferred worker confusion matrix (worker 1)***");
            for (int i = 0; i < labelCount; i++)
            {
                Console.WriteLine(result.WorkerConfusionMatrix[1][i].GetMean());
            }

            Console.WriteLine("\n***Inferred task time***");
            for (int i = 0; i < taskCount; i++)
            {
                Console.WriteLine("Task {0}: Short {1}, Long {2}", i, result.TaskShortTimePosterior[i], result.TaskLongTimePosterior[i]);
            }

#if usePvlScore
            Console.WriteLine("\n***Inferred worker propensity score***");
            for (int k = 0; k < workerCount; k++)
            {
                Console.WriteLine("Worker {0}: Propensity {1}", k, result.PropensityForValidLabellingScorePosterior[k]);
            }
#else
            Console.WriteLine("\n***Inferred worker propensity ***");
            for (int k = 0; k < workerCount; k++)
            {
                Console.WriteLine("Worker {0}: Propensity {1}", k, result.PropensityForValidLabellingPosterior[k]);
            }
#endif

            Console.WriteLine("\n***Inferred spammer label prob ***");
            Console.WriteLine("{0}", result.SpammerLabelProbPosterior.GetMode());

        }

    }
}
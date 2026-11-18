# AWS Deployment Plan (Dev, proposed)

Diagram: [aws-solace-ecs-deployment.svg](diagrams/aws-solace-ecs-deployment.svg). The diagram keeps its wording generic. Specific values are on this page: connection settings, producers, consumers, queue mapping, sizing numbers, delivery counts and worker setting names. Review this page with the pipeline and infrastructure team before requesting resources. Names use the DemoAir stand-ins; real values are supplied at deploy time only.

This worker runs the PNR-Linking feed only, so this plan covers one feed service.

Decoded-text detection is an initial POC: deploy it to Dev only, through the image built from the reviewed pull request. QA and production wait until a later change approves it (PR-005); for PNR-Linking, [tests/fixtures/parity-exceptions.json](../tests/fixtures/parity-exceptions.json) lists no miss. The recognizer file protects passenger names only, and `Presidio:DefaultDetection` is off in `appsettings.json`, so only the file's definitions act; set `Presidio__DefaultDetection=true` in a task definition to run Presidio's predefined recognizers as well (a reviewed task-definition deploy, like the other `Presidio` settings).

## How it works

Harness CI builds the repository `Dockerfile` into one image. A .NET 10 SDK stage publishes the worker, and the runtime stage adds CPython 3.12, `presidio-analyzer` and `presidio-anonymizer` 2.2.364, and the spaCy `en_core_web_lg` model. CI tags the image with the git commit and pushes it to Amazon ECR. Harness CD then registers the PNR-Linking task definition that points at that tag and rolls the ECS Fargate service onto it. The tasks have no load balancer and no inbound ports. At start-up, ECS reads the Solace password from Secrets Manager into `SOLACE_PASSWORD`, and the rest of the settings come from `appsettings.json` and task environment variables.

The PNR-Linking feed runs as its own ECS service. The service always keeps at least one task and scales out up to the number of partitions in the PNR-Linking input queue (see [Scalability](#scalability)). The input queue is partitioned by the owning record locator (`PNROwner`), so the link messages for one booking stay in order while tasks run in parallel. Each task reads the queue one message at a time over the native Solace client using TLS. It parses the JSON message with the shared System.Text.Json parser, detects and masks personal data in every JSON string through Presidio running in the same process via Python.NET, validates and serializes the output, and publishes it to the feed's output topic. The task acknowledges the input only after the broker confirms the publish. The worker sends a rejected message, or one delivered more than 5 times, to the feed's dead letter queue, never to output. As a backstop, the broker moves a message to the same DLQ after 10 deliveries. The PII path makes no HTTP calls and uses no remote services.

## Resource list

The Existing? column records what we assume; confirm each row with the platform team. Reuse an existing resource wherever one fits.

| # | Resource | Specification | Existing? |
|---|---|---|---|
| 1 | ECR repository | `presidio-worker`, scan on push, immutable tags; image about 1.8 GB, linux/amd64 | New |
| 2 | ECS cluster | Fargate capacity | Reuse if one exists |
| 3 | ECS task definition, 1 | `presidio-worker-pnr-linking-dev`; 1 vCPU, 4 GB; `stopTimeout` 40 s; `DOTNET_ENVIRONMENT=Dev`; `Messaging__Feed=PNR-Linking` and the PNR-Linking route (route 0); `Solace__*` environment variables; `secrets` entry for `SOLACE_PASSWORD`; `awslogs` driver with `awslogs-multiline-pattern` set to `^\S`, so each detection block (one header line, then indented lines) arrives as one log event; every other worker line starts in the first column | New (Harness registers it) |
| 4 | ECS service, 1 | The PNR-Linking service; min 1 task; no load balancer; rolling deploy | New |
| 4a | Service auto scaling, 1 | Min sized for the normal peak; scheduled actions raise it before holidays; CPU target tracking at 60 % with parallel consumers; max = the PNR-Linking queue's partition count | New |
| 5 | Task execution role | `ecr:GetAuthorizationToken` and image pull; `logs:CreateLogStream` and `PutLogEvents`; `secretsmanager:GetSecretValue` on the one secret; `kms:Decrypt` if a customer-managed key is used | New |
| 6 | Task role | Not needed, because the worker calls no AWS API | — |
| 7 | Secrets Manager secret | `piidemo/dev/solace`, JSON key `password`; the broker owner sets the value | New |
| 8 | KMS key | For the secret and the log group | Reuse an existing key, or the AWS-managed key |
| 9 | CloudWatch log group and alarms | `/ecs/presidio-worker-pnr-linking-dev`, with 30-day retention; CPU alarms for scaling; an alarm when the service's running task count drops below 1. With `Presidio__DetectionLog=true` the worker writes one detection block per message; without the multiline pattern in row 3, each block line is its own event, still adjacent and filterable by transaction ID. While both `Presidio__DetectionLog=true` and `Presidio__DetectionLogValues=true` are in effect (values are allowed in Dev or QA only), the detection blocks carry actual personal values. `DetectionLogValues` is on by default, so switching `Presidio__DetectionLog=true` on in Dev or QA starts writing values unless the task definition also sets `Presidio__DetectionLogValues=false`: before both are on, set the log group's retention to at most 7 days and limit reading to the PII team role; switch either flag only through a reviewed task-definition deploy, and restore the normal retention after the detection log or the values flag is off again (PR-006) | New |
| 10 | VPC and private subnets | Subnets that can route to the Solace broker | Reuse |
| 11 | Security group | No inbound rules; outbound TCP 55443 to the broker and 443 to the VPC endpoints | New |
| 12 | VPC endpoints, or NAT | `ecr.api`, `ecr.dkr`, S3 gateway, `logs`, `secretsmanager` (platform traffic only) | Reuse if present |
| 13 | Network path to the broker | The broker is in AWS in the same region: VPC peering or Transit Gateway to its account, plus the broker-side firewall rule for the ECS subnet CIDRs | Reuse if a path exists |
| 14 | Harness | CI pipeline (build and push), CD service and environment with an ECS rolling deploy, an AWS connector, and a delegate that can reach AWS | Confirm what exists |
| 15 | Harness deploy role | `ecs:RegisterTaskDefinition`, `UpdateService`, `DescribeServices`; `iam:PassRole` on the execution role; ECR push for CI | New, or extend an existing role |

## Scalability

**Target:** 400 messages per second for the PNR-Linking feed. This is the PNR-Linking share of the 2,000 messages per second sized for the airline feeds before this worker kept PNR-Linking only; the real PNR-Linking peak is still to be confirmed.

**Decided:**

- **Parallel consumers per task.** Each task opens several broker flows, so far fewer new tasks ever start. Built: set `Messaging__ConsumersPerTask` in the task definition, such as `8`, once the PNR-Linking queue is partitioned. All consumers share the task's one broker connection.
- **Numbers:** 8 consumers per task and 32 partitions, so at most 4 tasks for the PNR-Linking feed. If the feed needs more tasks, raise its partitions; no code change is needed.
- **Start tasks before they're needed.** The service's minimum is sized for the normal peak (one task). Scheduled scaling raises the minimum before holidays, so slow task start-up rarely matters. CPU target tracking at 60 % handles the rest, up to the partition count.

**What one message costs:** decoded-text detection runs local Presidio on every JSON string it does not decide by key or allow list. With `Presidio:DefaultDetection` off, as shipped, Presidio runs only the file's custom recognizer, `passengerName`, which has no context words, so spaCy does not run at all. In the detection spike (the production PNR-Linking sample, 20 rounds, one process, no broker) one task handled about 628 PNR-Linking messages per second in that mode; the previous spike, with every Presidio predefined recognizer and spaCy NER on and the earlier file, handled about 146 (133 on the workstation that measured 628). Runs on one workstation vary, so treat these as estimates. Of the 56 strings in the sample, the name keys, skipped keys and allow list decide 48, so Presidio analyzes 8 (the earlier file decided 54). A message with more free text costs more, a custom recognizer with context words makes spaCy run (without NER), and `Presidio__DefaultDetection=true` runs spaCy NER and every predefined recognizer again, back to about the earlier rate. Presidio calls are serialized inside a task, so extra consumers per task overlap broker round trips but do not add detection throughput; scale with tasks. At about 628 per second, detection alone handles 400 per second in 1 task; the broker round trips below still put the count at about 3 tasks, within the 4 tasks that 32 partitions and 8 consumers per task allow above. Confirm the real PNR-Linking peak before sizing. The figures come from `dotnet run --project tests/DetectionParityChecks -- spike` and are recorded in [tests/fixtures/spike-summary.json](../tests/fixtures/spike-summary.json). In production, the round trip to the broker adds to this.

The task counts and costs below were first sized for 2,000 messages per second; the figures here are one fifth of those, for the 400 per second PNR-Linking target, and do not include detection.

| | One consumer per task (`ConsumersPerTask=1`) | Several consumers per task (chosen) |
|---|---|---|
| What limits throughput | The wait on the broker; CPU stays low | CPU |
| Messages per second, one 1-vCPU task | 14–65, depending on broker distance; with detection, at most the spike rate above | About 100–200 before detection; with detection, at most the spike rate above |
| Tasks for 400 per second, with headroom | About 8 (broker in the same region) to 37 (broker about 30 ms away) | About 3–6 |
| Scaling signal | Queue depth, or a fixed task count | CPU target tracking at 60 % |
| Approximate Fargate cost at full scale | About $0.40–2.20 per hour | About $0.20–0.40 per hour |

`Messaging:ConsumersPerTask` opens several broker flows per task on the feed's partitioned queue. Each flow still reads one message at a time, so the order for each partition key holds. On an exclusive queue, the extra flows only wait as standby.

Limits to confirm before requesting infrastructure:

- **Broker:** about 400 persistent messages per second in and the same out; one connection per task, and one flow per consumer on top; enough partitions for the service's maximum number of consumers.
- **AWS:** a Fargate quota for the PNR-Linking task count above plus rolling deploys (about 3 tasks at the broker-bound rate above, so about 6 vCPU with a rolling deploy); about 20 free IP addresses in the subnets.
- **Start-up time:** a new task pulls a 1.8 GB image and loads the spaCy model before it takes messages. Keep the service's minimum task count sized for the normal peak, and use scheduled scaling before holidays.

Not measured yet: CPU per message on Fargate, real message sizes, and task start-up time. A load test in Dev should set the final task counts.

## Solace configuration

Configured in `appsettings.json` (`Environments:Dev`): host `tcps://…:55443`, VPN, username, client name and the password variable name. The PNR-Linking route is configured, as route 0, with DemoAir stand-in names in the PNR worker's convention: input `DEMOAIR.OPS.PIIDEMO.COS.PNRLINK.MI.1`, topic `demoair/ops/piidemo/pnrlink/protected`, dead letter queue `DEMOAIR.OPS.PIIDEMO.COS.PNRLINK.DLQ.1`. Real values are applied as environment variables at deploy time.

Confirmed:

- The broker is in AWS in the same region as the ECS tasks.
- PNR-Linking comes from the reservation systems through IBM MQ and Solace micro-integrations, as JSON in UTF-8 (CCSID 1208).
- The masked output goes to the partner core system.

Still needed from the broker owner:

- The real names of the PNR-Linking input queue, protected topic and dead letter queue, to replace the stand-ins at deploy time.
- A partitioned PNR-Linking input queue, 32 partitions, keyed by the owning record locator. The publishers must set the key: the micro-integration must read `PNROwner` from the JSON, or receive it in an MQ header; confirm it can.
- For the protected PNR-Linking topic: an output queue and its topic subscription, consumed by the partner core system. The partner should confirm it can take the PNR-Linking peak, and whether it needs the order kept for each booking. The order is kept for now as the safe default.
- The PNR-Linking dead letter queue, created by the broker team with the same access limits as its input queue, because it holds raw PII.
- The input queue's dead message queue set to the PNR-Linking DLQ, with max redelivery 10, as the broker backstop.
- An ACL profile for `piidemo-app` that can consume the PNR-Linking input queue, publish to the protected PNR-Linking topic, and send to the PNR-Linking DLQ.
- Delivery count enabled on the input queue. Without it, the worker cannot count failed deliveries and move a failing message to its DLQ.
- The broker CA certificate, if the broker does not use a public CA. It can be baked into the image under `/app/certs`, because it is not secret.

## Worker settings for the feed service

- **One feed per task:** set `Messaging__Feed=PNR-Linking` in the task definition. The process then consumes only the PNR-Linking route. When it is empty, the process runs every routed feed, which for this worker is also only PNR-Linking.
- **Unique client name per task:** `Solace:UniqueClientName` is on in `Dev`. It appends the host name and a random part to `Solace:ClientName`, so scaled-out tasks don't disconnect each other. The host name on Fargate is the task's private address, so the name is traceable without the ECS metadata endpoint, which is an HTTP call.
- **Routes:** the PNR-Linking route is route 0 (`Messaging__Routes__0__Input`, `…Output`, `…DeadLetter`). A task definition written for another worker, such as the PNR worker, names another feed and stops startup with a message naming the route index and the feed; rewrite it before deploying this image.

## Solace password from Secrets Manager

The worker already reads the password from the `SOLACE_PASSWORD` environment variable, so this needs no code change. The task definition maps the secret into that variable, and ECS fetches it when the task starts:

```json
"secrets": [
  { "name": "SOLACE_PASSWORD", "valueFrom": "arn:aws:secretsmanager:<region>:<account>:secret:piidemo/dev/solace-<suffix>:password::" }
]
```

Access needed: `secretsmanager:GetSecretValue` on that secret for the task execution role, plus `kms:Decrypt` if the secret uses a customer-managed key. To rotate the password, update the secret, then redeploy or restart the task.

## Build and pipeline status

- **Docker image:** the `Dockerfile` and `requirements.txt` in the repository contain the .NET and Python packages listed above. `deploy/publish-image.ps1` builds the image for linux/amd64 and pushes it to ECR. The image was not rebuilt while this page was prepared.
- **To confirm with the pipeline team:** whether Harness CI builds this `Dockerfile` successfully, whether it pushes to ECR, and what remains for the ECS deploy stage (the service, the task definition source, the AWS connector and the delegate).

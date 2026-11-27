/**
 * What the ediabas_* tools need from a backend: the VM agent (AgentClient) or the native engine
 * (NativeClient, `ediabasd serve` as a child process). Both speak docs/api.md's shapes.
 */
import type { DescribeJob, DescribeList, EcuList, JobResult, JobsResponse } from "./agentClient.js";

export interface JobRequest {
  ecu: string;
  job: string;
  args?: string | undefined;
  results?: string | undefined;
}

export interface EdiabasBackend {
  readonly kind: "vm" | "native";
  listEcus(filter?: string): Promise<EcuList>;
  describeJobs(q: { ecu: string; filter?: string | undefined; comments?: boolean | undefined }): Promise<DescribeList>;
  describeJob(q: { ecu: string; job: string }): Promise<DescribeJob>;
  runJob(body: { ecu: string; job: string; args?: string | undefined; results?: string | undefined; timeoutSeconds?: number | undefined }): Promise<JobResult>;
  /** 1..20 items; the caller chunks. `timeoutSeconds` applies to each item. */
  runJobs(items: JobRequest[], timeoutSeconds?: number): Promise<JobsResponse>;
}

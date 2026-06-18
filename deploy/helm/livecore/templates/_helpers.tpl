{{/*
Template helpers for the LiveCore chart (CORE-DEP-004).
*/}}

{{/* The base name, overridable with nameOverride. */}}
{{- define "livecore.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* The fully-qualified release name, overridable with fullnameOverride. */}}
{{- define "livecore.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* The chart name and version, for the helm.sh/chart label. */}}
{{- define "livecore.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* Common labels applied to every object. */}}
{{- define "livecore.labels" -}}
helm.sh/chart: {{ include "livecore.chart" . }}
{{ include "livecore.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{/* Release-level selector labels. */}}
{{- define "livecore.selectorLabels" -}}
app.kubernetes.io/name: {{ include "livecore.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{/*
Per-component selector labels. Call with a dict carrying the root context and the
component name, e.g. (dict "root" $ "component" "api"), so a Deployment's
selector/pod labels match one component only.
*/}}
{{- define "livecore.componentSelectorLabels" -}}
app.kubernetes.io/name: {{ include "livecore.name" .root }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{/*
A fully-qualified image reference for a component. Call with a dict carrying the
root context and the component values block, e.g.
(dict "root" $ "component" .Values.api). The tag falls back to the component tag,
then the shared image.tag, then the chart appVersion.
*/}}
{{- define "livecore.componentImage" -}}
{{- $registry := .root.Values.image.registry -}}
{{- $repository := .component.image.repository -}}
{{- $tag := .component.image.tag | default .root.Values.image.tag | default .root.Chart.AppVersion -}}
{{- if $registry -}}
{{- printf "%s/%s:%s" $registry $repository $tag -}}
{{- else -}}
{{- printf "%s:%s" $repository $tag -}}
{{- end -}}
{{- end -}}

{{/* The ServiceAccount name to use. */}}
{{- define "livecore.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "livecore.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/* The Secret name the configuration is projected from. */}}
{{- define "livecore.secretName" -}}
{{- if .Values.secrets.existingSecret -}}
{{- .Values.secrets.existingSecret -}}
{{- else -}}
{{- printf "%s-secret" (include "livecore.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/*
The envFrom block that projects the documented configuration contract
(CORE-OPS-008) into a container: the non-secret ConfigMap and the secret Secret.
The migrations runner, the API and the worker all read the SAME keys
(ConnectionStrings__Database, OIDC, S3, CORS, ...) this way.
*/}}
{{- define "livecore.envFrom" -}}
- configMapRef:
    name: {{ include "livecore.fullname" . }}-config
- secretRef:
    name: {{ include "livecore.secretName" . }}
{{- end -}}

{{/*
The host-header allow-list rendered into the ConfigMap (CORE-SEC-010). ASP.NET Core treats an
EMPTY AllowedHosts as allow-all ("*" — host-header validation off), so the chart never renders it
empty: this is the operator's configured public host(s) (config.AllowedHosts) JOINED with the
loopback hosts the in-pod kubelet liveness/readiness probes send their Host header as (the api/worker
probes set "Host: localhost"). Keeping the loopback hosts in the allow-list means a restrictive,
real-host allow-list still admits the probes (the kubelet hits the dynamic pod IP, which no static
allow-list could name). With nothing configured it is just the loopback hosts — non-empty, so host
filtering is ON by default and a default install already rejects an unexpected Host header. A
configured "*" (an explicit allow-all) is refused and falls back to the loopback hosts, so the chart
cannot be coaxed into rendering an allow-all host filter. Override config.AllowedHosts with your
ingress host(s) (semicolon-separated) for production traffic; the loopback probe hosts stay appended.
*/}}
{{- define "livecore.allowedHosts" -}}
{{- $loopback := "localhost;127.0.0.1" -}}
{{- $configured := .Values.config.AllowedHosts | toString | trim -}}
{{- if and $configured (ne $configured "*") -}}
{{- printf "%s;%s" $configured $loopback -}}
{{- else -}}
{{- $loopback -}}
{{- end -}}
{{- end -}}

{{/*
Fail the render when the API can run past a single replica without a realtime backplane
(CORE-DEP-009). Running more than one API pod on the in-process backplane silently drops
cross-pod realtime delivery (CORE-OPS-007): a reveal computed on one pod never reaches
clients connected to another. The API runs multiple pods two ways, and BOTH require the
backplane: a manual api.replicaCount > 1, and an enabled HorizontalPodAutoscaler
(autoscaling.enabled, CORE-DEP-011) which can scale the Deployment past one pod. Either MUST
set secrets.Realtime__Backplane__ConnectionString (or project it through
secrets.existingSecret). When an existingSecret is used the chart cannot inspect its
contents, so the guard defers to it. Invoked from the API Deployment (and the HPA) so it runs
on every render (helm template / helm install / helm lint), failing fast instead of silently
shipping a broken multi-replica realtime topology.
*/}}
{{- define "livecore.validateRealtimeBackplane" -}}
{{- $multiReplica := gt (int .Values.api.replicaCount) 1 -}}
{{- $autoscaling := .Values.autoscaling.enabled -}}
{{- if or $multiReplica $autoscaling -}}
{{- if not .Values.secrets.existingSecret -}}
{{- if not .Values.secrets.Realtime__Backplane__ConnectionString -}}
{{- if $autoscaling -}}
{{- fail (printf "\n\nERROR (CORE-DEP-009): autoscaling.enabled is true but no realtime backplane is configured.\nThe HorizontalPodAutoscaler (CORE-DEP-011) can scale the API past one pod, and running more than\none API replica on the in-process backplane silently drops cross-pod realtime delivery: a reveal\ncomputed on one pod never reaches clients on another (CORE-OPS-007).\n\nTo autoscale the API, configure the Redis/Valkey backplane:\n  --set-string secrets.Realtime__Backplane__ConnectionString=<redis-connection-string>\nand enable SignalR sticky sessions at the ingress (CORE-DEP-002), or supply the\nbackplane through secrets.existingSecret. To run without autoscaling, set autoscaling.enabled=false.") -}}
{{- else -}}
{{- fail (printf "\n\nERROR (CORE-DEP-009): api.replicaCount is %d but no realtime backplane is configured.\nRunning more than one API replica on the in-process backplane silently drops cross-pod\nrealtime delivery: a reveal computed on one pod never reaches clients on another (CORE-OPS-007).\n\nTo run multiple API replicas, configure the Redis/Valkey backplane:\n  --set-string secrets.Realtime__Backplane__ConnectionString=<redis-connection-string>\nand enable SignalR sticky sessions at the ingress (CORE-DEP-002), or supply the\nbackplane through secrets.existingSecret. To run a single instance, set api.replicaCount=1." (int .Values.api.replicaCount)) -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- end -}}

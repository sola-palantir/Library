import { useState } from "react";
import { api } from "./types";
import { localTime } from "./time";

export type Connection = {
  configured: boolean;
  state: string;
  folder: string | null;
  apiOrigin: string | null;
  accessExpiresUtc: string | null;
  sessionExpiresUtc: string | null;
  canConnect: boolean;
  canDisconnect: boolean;
  rememberedAccess: boolean;
};
const time = (value: string | null) =>
  value ? localTime(value) : "Not authorized";

export function WorkerConnection({
  connection,
  folder,
  changed,
}: {
  connection: Connection;
  folder: string;
  changed: () => void;
}) {
  const [dialog, setDialog] = useState<"connect" | "revoke" | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const labels: Record<string, string> = {
    "not-configured": "Local authorization required",
    disconnected: "Disconnected",
    "waiting-for-worker": "Waiting for Windows worker",
    connected: "Connected for read-only scanning",
    "authorization-expired": "Authorization expired",
    "session-expired":
      "Connection expired — reconnect within the approved authorization",
    revoked: "Access revoked",
  };
  async function action(name: "connect" | "disconnect" | "revoke") {
    setBusy(true);
    setError("");
    try {
      await api(
        `/review-connection/${name}`,
        name === "connect" ? { folder: connection.folder } : {},
      );
      setDialog(null);
      setConfirmed(false);
      changed();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <section aria-label="Windows worker connection">
      <h3>Windows worker</h3>
      <p role="status">
        {labels[connection.state] ?? "Connection unavailable"}
      </p>
      <p>
        Folder scope:{" "}
        <strong>
          {connection.folder || folder || "Choose a dedicated sample folder"}
        </strong>
        . Up to eight files directly in this folder; subfolders are excluded.
      </p>
      {connection.configured && (
        <p>
          Authorization ends {time(connection.accessExpiresUtc)}.{" "}
          {connection.sessionExpiresUtc &&
            `This connection ends ${time(connection.sessionExpiresUtc)}.`}
        </p>
      )}
      <div className="media-controls">
        <button
          disabled={busy || connection.canDisconnect}
          onClick={() => {
            setConfirmed(false);
            setDialog("connect");
          }}
        >
          Connect Windows worker
        </button>
        <button
          disabled={busy || !connection.canDisconnect}
          onClick={() => void action("disconnect")}
        >
          Disconnect and cancel scans
        </button>
        {connection.rememberedAccess && (
          <button
            disabled={busy}
            onClick={() => {
              setConfirmed(false);
              setDialog("revoke");
            }}
          >
            Forget access
          </button>
        )}
      </div>
      {connection.state === "waiting-for-worker" && (
        <p>
          Open the locally authorized Windows companion. If it stopped or lost
          contact, disconnect here, reopen it, then connect again. Reconnecting
          never extends the authorization expiry.
        </p>
      )}
      {["disconnected", "session-expired"].includes(connection.state) && (
        <p>
          Open the Windows companion, then choose Connect Windows worker to
          reconnect. Saved reviews remain available while disconnected.
        </p>
      )}
      {dialog && (
        <div
          role="dialog"
          aria-modal="false"
          aria-label={
            dialog === "revoke"
              ? "Revoke read-only access"
              : "Connect read-only worker"
          }
          className="media-card"
        >
          <h4>
            {dialog === "revoke"
              ? "Forget this authorization"
              : "Review connection scope"}
          </h4>
          <p>{connection.folder || folder || "No folder approved"}</p>
          {dialog === "revoke" ? (
            <p>
              This cancels scans and revokes this authorization in the catalog,
              including after restart. Saved reviews remain. The encrypted
              Windows file is not deleted, but cannot reconnect; a new local
              authorization is required.
            </p>
          ) : connection.canConnect ? (
            <>
              <p>
                Connect only to {connection.apiOrigin}, for up to one hour,
                ending no later than {time(connection.accessExpiresUtc)}. The
                manually opened Windows worker reads metadata only. No encoding,
                copying, media upload, file changes or background startup.
              </p>
              <p>
                Windows stores the approved credential encrypted for your
                Windows account. The API stores only its hash. Disconnect
                cancels scans; Forget access revokes the authorization.
              </p>
            </>
          ) : (
            <p>
              No usable local authorization is configured. This button cannot
              create credentials or renew expired access. A separate approval is
              required before the Windows companion can remember this folder,
              exact localhost API and expiry. Saved reviews remain usable. See
              the read-only connection setup guide.
            </p>
          )}
          {(dialog === "revoke" || connection.canConnect) && (
            <label>
              <input
                type="checkbox"
                checked={confirmed}
                onChange={(e) => setConfirmed(e.target.checked)}
              />
              {dialog === "revoke"
                ? "Revoke this read-only authorization"
                : "Connect within this approved folder and expiry"}
            </label>
          )}
          <div className="media-controls">
            {(dialog === "revoke" || connection.canConnect) && (
              <button
                disabled={busy || !confirmed}
                onClick={() =>
                  void action(dialog === "revoke" ? "revoke" : "connect")
                }
              >
                {dialog === "revoke"
                  ? "Revoke access now"
                  : "Connect read-only"}
              </button>
            )}
            <button disabled={busy} onClick={() => setDialog(null)}>
              Close
            </button>
          </div>
        </div>
      )}
      {error && (
        <p role="alert">
          {error} Connection outcome may be uncertain after a timeout; check
          status before retrying.
        </p>
      )}
    </section>
  );
}

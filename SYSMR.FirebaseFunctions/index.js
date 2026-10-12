const crypto = require("node:crypto");
const admin = require("firebase-admin");
const { onRequest } = require("firebase-functions/v2/https");

admin.initializeApp();

const normalizeCustomerNumber = (value) => value.trim().toUpperCase();

const createLoginEmail = (customerNumber) => {
  const digest = crypto
    .createHash("sha256")
    .update(normalizeCustomerNumber(customerNumber), "utf8")
    .digest("hex");
  return `store-${digest}@accounts.sysmr.app`;
};

exports.provisionStore = onRequest({ region: "us-central1" }, async (request, response) => {
  if (request.method !== "POST") {
    response.set("Allow", "POST").status(405).json({ error: { message: "method-not-allowed" } });
    return;
  }

  try {
    const authorization = request.get("authorization") || "";
    const token = authorization.startsWith("Bearer ") ? authorization.slice(7) : "";
    if (!token) {
      response.status(401).json({ error: { message: "authentication-required" } });
      return;
    }

    const caller = await admin.auth().verifyIdToken(token);
    const allowedUid = process.env.SYSMR_SUPER_ADMIN_UID;
    if (!allowedUid || caller.uid !== allowedUid) {
      response.status(403).json({ error: { message: "super-admin-required" } });
      return;
    }

    const { customerNumber, customerName, password, storeType = "sales" } = request.body || {};
    if (typeof customerNumber !== "string" ||
        !/^[A-Z0-9-]{3,32}$/.test(normalizeCustomerNumber(customerNumber)) ||
        typeof customerName !== "string" ||
        customerName.trim().length < 1 ||
        customerName.trim().length > 120 ||
        typeof password !== "string" ||
        password.length < 8 ||
        password.length > 128 ||
        !["sales", "restaurant"].includes(storeType)) {
      response.status(400).json({ error: { message: "invalid-account-data" } });
      return;
    }

    const normalizedNumber = normalizeCustomerNumber(customerNumber);
    const user = await admin.auth().createUser({
      email: createLoginEmail(normalizedNumber),
      password,
      disabled: false,
    });

    try {
      await admin.database().ref(`clients/${user.uid}`).set({
        customerNumber: normalizedNumber,
        customerName: customerName.trim(),
        storeType,
        updatedAt: new Date().toISOString(),
      });
    } catch (error) {
      await admin.auth().deleteUser(user.uid);
      throw error;
    }

    response.status(201).json({ uid: user.uid });
  } catch (error) {
    if (error.code === "auth/email-already-exists") {
      response.status(409).json({ error: { message: "customer-number-already-exists" } });
      return;
    }
    console.error("Store account provisioning failed:", error);
    response.status(500).json({ error: { message: "store-account-provisioning-failed" } });
  }
});

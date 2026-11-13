const crypto = require("node:crypto");
const admin = require("firebase-admin");
const { onRequest } = require("firebase-functions/v2/https");

admin.initializeApp();

const normalizeCustomerNumber = (value) => value.trim().toUpperCase();

const isValidEmail = (value) =>
  typeof value === "string" && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value) && value.length <= 254;

const findStoreByNumber = async (customerNumber) => {
  const snapshot = await admin
    .database()
    .ref("clients")
    .orderByChild("customerNumber")
    .equalTo(normalizeCustomerNumber(customerNumber))
    .limitToFirst(1)
    .get();
  if (!snapshot.exists()) {
    return null;
  }
  const [uid, profile] = Object.entries(snapshot.val())[0];
  return { uid, profile };
};

const verifySuperAdmin = async (request, response) => {
  const authorization = request.get("authorization") || "";
  const token = authorization.startsWith("Bearer ") ? authorization.slice(7) : "";
  if (!token) {
    response.status(401).json({ error: { message: "authentication-required" } });
    return null;
  }

  try {
    const caller = await admin.auth().verifyIdToken(token);
    const allowedUid = process.env.SYSMR_SUPER_ADMIN_UID;
    if (!allowedUid || caller.uid !== allowedUid) {
      response.status(403).json({ error: { message: "super-admin-required" } });
      return null;
    }
    return caller;
  } catch (error) {
    response.status(401).json({ error: { message: "invalid-token" } });
    return null;
  }
};

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
    const caller = await verifySuperAdmin(request, response);
    if (!caller) {
      return;
    }

    const { customerNumber, customerName, password, storeType = "sales", contactEmail } = request.body || {};
    if (typeof customerNumber !== "string" ||
        !/^[A-Z0-9-]{3,32}$/.test(normalizeCustomerNumber(customerNumber)) ||
        typeof customerName !== "string" ||
        customerName.trim().length < 1 ||
        customerName.trim().length > 120 ||
        typeof password !== "string" ||
        password.length < 8 ||
        password.length > 128 ||
        !["sales", "restaurant"].includes(storeType) ||
        (contactEmail !== undefined && contactEmail !== null && !isValidEmail(contactEmail))) {
      response.status(400).json({ error: { message: "invalid-account-data" } });
      return;
    }

    const normalizedNumber = normalizeCustomerNumber(customerNumber);
    const authEmail = contactEmail ? contactEmail.trim().toLowerCase() : createLoginEmail(normalizedNumber);
    const user = await admin.auth().createUser({
      email: authEmail,
      password,
      disabled: false,
    });

    try {
      await admin.database().ref(`clients/${user.uid}`).set({
        customerNumber: normalizedNumber,
        customerName: customerName.trim(),
        storeType,
        ...(contactEmail ? { contactEmail: authEmail } : {}),
        updatedAt: new Date().toISOString(),
      });
    } catch (error) {
      await admin.auth().deleteUser(user.uid);
      throw error;
    }

    response.status(201).json({ uid: user.uid });
  } catch (error) {
    if (error.code === "auth/email-already-exists") {
      response.status(409).json({ error: { message: "customer-number-or-email-already-exists" } });
      return;
    }
    console.error("Store account provisioning failed:", error);
    response.status(500).json({ error: { message: "store-account-provisioning-failed" } });
  }
});

// Resolve o e-mail de autenticação da loja a partir do número + nome, para que o
// login e a recuperação de senha funcionem quando a loja usa um e-mail de contato
// real no lugar do e-mail sintético.
exports.resolveStoreLogin = onRequest({ region: "us-central1", invoker: "public" }, async (request, response) => {
  if (request.method !== "POST") {
    response.set("Allow", "POST").status(405).json({ error: { message: "method-not-allowed" } });
    return;
  }

  try {
    const { customerNumber, customerName } = request.body || {};
    if (typeof customerNumber !== "string" ||
        !/^[A-Z0-9-]{3,32}$/.test(normalizeCustomerNumber(customerNumber)) ||
        typeof customerName !== "string" ||
        customerName.trim().length < 1 ||
        customerName.trim().length > 120) {
      response.status(400).json({ error: { message: "invalid-account-data" } });
      return;
    }

    const store = await findStoreByNumber(customerNumber);
    if (!store ||
        String(store.profile.customerName || "").trim().toLowerCase() !== customerName.trim().toLowerCase()) {
      response.status(404).json({ error: { message: "store-not-found" } });
      return;
    }

    const user = await admin.auth().getUser(store.uid);
    const email = user.email || createLoginEmail(normalizeCustomerNumber(customerNumber));
    response.status(200).json({
      email,
      hasContactEmail: !email.endsWith("@accounts.sysmr.app"),
    });
  } catch (error) {
    console.error("Store login resolution failed:", error);
    response.status(500).json({ error: { message: "store-login-resolution-failed" } });
  }
});

// Permite ao Super Admin local (com PIN do SYSMR) definir o e-mail de contato
// da loja sem precisar das credenciais globais do Firebase. Requer o UID da loja.
exports.setStoreContactEmailByUid = onRequest({ region: "us-central1" }, async (request, response) => {
  if (request.method !== "POST") {
    response.set("Allow", "POST").status(405).json({ error: { message: "method-not-allowed" } });
    return;
  }

  try {
    const { storeUid, contactEmail, localPin } = request.body || {};
    if (typeof storeUid !== "string" || storeUid.length < 10) {
      response.status(400).json({ error: { message: "invalid-store-uid" } });
      return;
    }

    // Valida o PIN local do SYSMR (hash SHA256 do PIN definido no .env)
    const expectedPinHash = process.env.SYSMR_LOCAL_PIN_HASH;
    if (!expectedPinHash) {
      response.status(500).json({ error: { message: "local-pin-not-configured" } });
      return;
    }
    const pinHash = crypto.createHash("sha256").update(String(localPin || ""), "utf8").digest("hex");
    if (pinHash !== expectedPinHash) {
      response.status(403).json({ error: { message: "invalid-local-pin" } });
      return;
    }

    const hasContactEmail = typeof contactEmail === "string" && contactEmail.trim().length > 0;
    if (hasContactEmail && !isValidEmail(contactEmail)) {
      response.status(400).json({ error: { message: "invalid-email" } });
      return;
    }

    const authEmail = hasContactEmail
      ? contactEmail.trim().toLowerCase()
      : createLoginEmail(storeUid); // fallback: usa o UID como "número" para gerar e-mail sintético

    await admin.auth().updateUser(storeUid, { email: authEmail });
    await admin.database().ref(`clients/${storeUid}`).update({
      contactEmail: hasContactEmail ? authEmail : null,
      updatedAt: new Date().toISOString(),
    });

    response.status(200).json({ email: authEmail, hasContactEmail });
  } catch (error) {
    if (error.code === "auth/email-already-exists") {
      response.status(409).json({ error: { message: "email-already-exists" } });
      return;
    }
    console.error("Set store contact email by UID failed:", error);
    response.status(500).json({ error: { message: "set-contact-email-failed" } });
  }
});

// Permite ao Super Admin global cadastrar/alterar/remover o e-mail de contato da
// loja. O e-mail passa a ser o identificador de autenticação e o destino do link
// de redefinição de senha enviado pelo Firebase.
exports.updateStoreContactEmail = onRequest({ region: "us-central1" }, async (request, response) => {
  if (request.method !== "POST") {
    response.set("Allow", "POST").status(405).json({ error: { message: "method-not-allowed" } });
    return;
  }

  try {
    const caller = await verifySuperAdmin(request, response);
    if (!caller) {
      return;
    }

    const { customerNumber, contactEmail } = request.body || {};
    const emailIsEmpty = contactEmail === undefined || contactEmail === null || contactEmail === "";
    if (typeof customerNumber !== "string" ||
        !/^[A-Z0-9-]{3,32}$/.test(normalizeCustomerNumber(customerNumber)) ||
        (!emailIsEmpty && !isValidEmail(contactEmail))) {
      response.status(400).json({ error: { message: "invalid-account-data" } });
      return;
    }

    const store = await findStoreByNumber(customerNumber);
    if (!store) {
      response.status(404).json({ error: { message: "store-not-found" } });
      return;
    }

    const normalizedNumber = normalizeCustomerNumber(customerNumber);
    const hasContactEmail = typeof contactEmail === "string" && contactEmail.trim().length > 0;
    const authEmail = hasContactEmail
      ? contactEmail.trim().toLowerCase()
      : createLoginEmail(normalizedNumber);
    await admin.auth().updateUser(store.uid, { email: authEmail });
    await admin.database().ref(`clients/${store.uid}`).update({
      contactEmail: hasContactEmail ? authEmail : null,
      updatedAt: new Date().toISOString(),
    });

    response.status(200).json({ email: authEmail, hasContactEmail });
  } catch (error) {
    if (error.code === "auth/email-already-exists") {
      response.status(409).json({ error: { message: "email-already-exists" } });
      return;
    }
    console.error("Store contact email update failed:", error);
    response.status(500).json({ error: { message: "store-contact-email-update-failed" } });
  }
});

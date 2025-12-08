use ed25519_dalek::{SigningKey, Signer};
use x25519_dalek::{StaticSecret};
use rand::rngs::OsRng;

pub struct NodeIdentity {
    pub identity_keypair: SigningKey,  // Holds both Public and Private keys for Identity
    pub onion_secret: StaticSecret,    // X25519 Private key
}


impl NodeIdentity {

    /// Generates a new random identity
    pub fn generate() -> Self {
        let mut csprng = OsRng;
        let identity_keypair = SigningKey::generate(&mut csprng);
        let onion_secret = StaticSecret::random_from_rng(&mut csprng);

        Self {
            identity_keypair,
            onion_secret,
        }
    }

    /// Signs a handshake payload with the identity key
    pub fn sign_handshake(
        &self,
        timestamp: u64
    ) -> super::handshake::HandshakePayload {
        let identity_pub = self.identity_keypair.verifying_key();
        let onion_pub = x25519_dalek::PublicKey::from(&self.onion_secret);

        let mut message = [0u8; 72];
        message[0..32].copy_from_slice(identity_pub.as_bytes());
        message[32..64].copy_from_slice(onion_pub.as_bytes());
        message[64..72].copy_from_slice(&timestamp.to_be_bytes());

        let signature = self.identity_keypair.sign(&message);

        super::handshake::HandshakePayload {
            identity_key: identity_pub,
            onion_key: onion_pub,
            timestamp,
            signature,
        }
    }
}
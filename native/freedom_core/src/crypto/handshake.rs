use ed25519_dalek::{ Signer, Verifier, Signature };
use x25519_dalek::{ PublicKey as X25519PublicKey };
use std::convert::TryInto;

const IDENTITY_KEY_SIZE: usize = 32;
const ONION_KEY_SIZE: usize = 32;
const TIMESTAMP_SIZE: usize = 8;
const SIGNATURE_SIZE: usize = 64;
const HANDSHAKE_PAYLOAD_SIZE: usize = 136;

#[derive(Debug, Clone)]
pub struct HandshakePayload {
    pub identity_key: ed25519_dalek::VerifyingKey, // Public key for Identity
    pub onion_key: X25519PublicKey, // Public key for Onion routing
    pub timestamp: u64, // Timestamp in seconds since UNIX epoch
    pub signature: Signature, // Signature of the handshake payload
}

#[derive(Debug, thiserror::Error)]
pub enum HandshakeError {
    #[error("Invalid payload size: expected {expected}, got {got}")] InvalidSize {
        expected: usize,
        got: usize,
    },
    #[error("Invalid identity key bytes")]
    InvalidIdentityKey,
    #[error("Invalid onion key bytes")]
    InvalidOnionKey,
    #[error("Invalid signature bytes")]
    InvalidSignature,
    #[error("Signature verification failed")]
    VerificationFailed,
}

impl HandshakePayload {

    /// Serialize the payload to a bytes array
    /// Format: [identity_key (32 bytes) | onion_key (32 bytes) | timestamp (8 bytes) | signature (64 bytes)]
    pub fn to_bytes(&self) -> [u8; HANDSHAKE_PAYLOAD_SIZE] {
        let mut bytes = [0u8; HANDSHAKE_PAYLOAD_SIZE];
        
        // Optimize: Direct slice mapping using standard copy logic
        bytes[0..32].copy_from_slice(self.identity_key.as_bytes());
        bytes[32..64].copy_from_slice(self.onion_key.as_bytes());

        bytes[64..72].copy_from_slice(&self.timestamp.to_be_bytes());
        bytes[72..136].copy_from_slice(&self.signature.to_bytes());

        bytes
    }

    /// Deserialize the payload from a bytes array
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, HandshakeError> {
        if bytes.len() != HANDSHAKE_PAYLOAD_SIZE {
            return Err(HandshakeError::InvalidSize {
                expected: HANDSHAKE_PAYLOAD_SIZE,
                got: bytes.len(),
            });
        }

        let identity_key = ed25519_dalek::VerifyingKey
            ::from_bytes(bytes[0..32].try_into().unwrap())
            .map_err(|_| HandshakeError::InvalidIdentityKey)?;

        let onion_key = X25519PublicKey::from(<[u8; 32]>::try_from(&bytes[32..64]).unwrap());

        let timestamp = u64::from_be_bytes(bytes[64..72].try_into().unwrap());

        let signature = Signature::from_bytes(bytes[72..136].try_into().unwrap());

        Ok(Self {
            identity_key,
            onion_key,
            timestamp,
            signature,
        })
    }

    /// Verify the signature of the handshake payload
    pub fn verify(&self) -> Result<(), HandshakeError> {
        let mut message = [0u8; 32 + 32 + 8];
        message[0..32].copy_from_slice(self.identity_key.as_bytes());
        message[32..64].copy_from_slice(self.onion_key.as_bytes());
        message[64..72].copy_from_slice(&self.timestamp.to_be_bytes());

        self.identity_key
            .verify(&message, &self.signature)
            .map_err(|_| HandshakeError::VerificationFailed)
    }
}

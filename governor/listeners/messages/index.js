import { sampleMessageCallback } from './sample-message.js';

export const register = (app) => {
  app.message(/\b(hi|hello|hey)\b/i, sampleMessageCallback);
};

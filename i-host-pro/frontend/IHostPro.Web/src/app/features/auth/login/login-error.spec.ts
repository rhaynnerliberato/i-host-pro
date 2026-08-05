import { isInvalidCredentialsError } from './login-error';

describe('isInvalidCredentialsError', () => {
  it('recognizes a deserialized ProblemDetails-shaped object with status 401', () => {
    expect(isInvalidCredentialsError({ status: 401, title: 'Unauthorized' })).toBe(true);
  });

  it('recognizes an ApiException-like object (Error subclass with a status field) with status 401', () => {
    class ApiExceptionLike extends Error {
      status: number;
      constructor(status: number) {
        super('Unauthorized');
        this.status = status;
      }
    }
    expect(isInvalidCredentialsError(new ApiExceptionLike(401))).toBe(true);
  });

  it('treats a 500 status as a generic error, not invalid credentials', () => {
    expect(isInvalidCredentialsError({ status: 500 })).toBe(false);
  });

  it('treats an object with no status field as a generic error', () => {
    expect(isInvalidCredentialsError({ message: 'boom' })).toBe(false);
  });

  it('never throws for null/undefined/string values and treats them as a generic error', () => {
    expect(isInvalidCredentialsError(null)).toBe(false);
    expect(isInvalidCredentialsError(undefined)).toBe(false);
    expect(isInvalidCredentialsError('some string')).toBe(false);
  });
});
